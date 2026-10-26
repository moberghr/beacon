---
title: Embed Beacon on SQL Server with Hangfire
description: "Beacon inside your ASP.NET Core app with its own data in SQL Server and its scheduled jobs on Hangfire: what differs from the PostgreSQL and Warp recipe, and the IBeaconScheduler you write for Hangfire."
---

| | |
|---|---|
| **You end up with** | Your ASP.NET Core app serving Beacon at `/beacon`, Beacon's schema created in SQL Server, and subscriptions, data-quality checks and maintenance running as Hangfire jobs. |
| **Time** | about 40 minutes |
| **You need** | An ASP.NET Core app on .NET 10, the [PostgreSQL and Warp recipe](/cookbooks/embed-postgresql-warp/) open beside this one, an empty SQL Server database for Beacon, and a login that may create a schema in it. |
| **Applies to** | Beacon 5.0 · Full Beacon |

:::tip[Let your AI do it]
Paste this into Claude Code or another coding agent, in the repository of your app.

```text wrap
Read https://moberghr.github.io/beacon/cookbooks/embed-sqlserver-hangfire.md and follow it step by step in this repository.
It lists only what differs from https://moberghr.github.io/beacon/cookbooks/embed-postgresql-warp.md, so read both.
Ask me before every decision either one marks as one, and show me the result of each "Check it works" item.
Never write a connection string, the encryption key or the setup token into a committed file. Tell me clearly that the
Hangfire scheduler is code we write in our host: Beacon does not ship one.
```
:::

This recipe lists only the differences from [the PostgreSQL and Warp recipe](/cookbooks/embed-postgresql-warp/). Follow that recipe for the secrets, the middleware order, first-run setup and the first data source, and swap in what is below.

## Before you start

| Decision | Options | Default |
|---|---|---|
| Hangfire's storage | The Beacon database (Hangfire keeps its tables in its own `HangFire` schema), or a separate SQL Server database | A separate database if you can; the Beacon database otherwise |
| Which data sources you monitor | Add the matching connector for each engine. `Moberg.Beacon.Connector.SqlServer` for SQL Server | The engines you actually monitor |
| Several app instances | Run Beacon's migrations once from a deployment step, not from every replica | A deployment step |

## 1. Swap the packages

```bash
dotnet add package Moberg.Beacon.Core.SqlServer
dotnet add package Moberg.Beacon.Api
dotnet add package Moberg.Beacon.UI
dotnet add package Moberg.Beacon.AI
dotnet add package Moberg.Beacon.MCP
dotnet add package Moberg.Beacon.Connector.SqlServer
dotnet add package Hangfire.AspNetCore
dotnet add package Hangfire.SqlServer
```

`Moberg.Beacon.Core.SqlServer` stores **Beacon's own data** in SQL Server. `Moberg.Beacon.Connector.SqlServer` lets Beacon **monitor** a SQL Server database. Add the connector only if you monitor one. The packages `Moberg.Warp.*`, `EFCore.NamingConventions` and the `Warp*` files are not needed.

## 2. Change the registration

In the `AddBeaconServices` chain, replace the PostgreSQL calls:

```csharp
using Beacon.Connector.SqlServer;   // AddSqlServerConnector
using Beacon.Core.SqlServer;        // UseSqlServer

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<HangfireBeaconScheduler>();   // the class from step 3
        // ... the rest of the options as in the PostgreSQL recipe ...
    })
    .AddSqlServerConnector()
    .UseSqlServer();                 // reads ConnectionStrings:BeaconContext; must end the chain
```

`ConnectionStrings:BeaconContext` is now a SQL Server connection string. Everything lives in the fixed schema `beacon`; `Beacon:Schema` accepts nothing else.

Replace `AddBeaconWarp(...)` and `StartBeaconWarpAsync()` with Hangfire's registration and the startup of step 4:

```csharp
builder.Services.AddHangfire(x => x.UseSqlServerStorage(builder.Configuration.GetConnectionString("HangfireContext")
    ?? builder.Configuration.GetConnectionString("BeaconContext")));
builder.Services.AddHangfireServer();
```

Keep `AddBeaconHostInfrastructure<...>()`, the cookie, Data Protection, API, MCP and UI calls and the whole middleware pipeline exactly as in the other recipe. Data Protection keeps its keys in Beacon's database on either provider.

## 3. Write the Hangfire scheduler

Beacon ships no job runner and no Hangfire integration. `IBeaconScheduler` is the contract between Beacon and your runner, and this class is **your code**, in your host. The members, from `src/Beacon.Core/Worker/IBeaconScheduler.cs`:

| Member | Needed for |
|---|---|
| `AddOrUpdate(subscriptionId, subscriptionName, cron)` and `Remove(subscriptionId, subscriptionName)` | Subscriptions. Required: they have no default body. |
| `AddOrUpdateDataQualityJob(contractId, contractName, cron)` and `RemoveDataQualityJob(contractId, contractName)` | Data contracts. Throw `NotImplementedException` until you implement them. |
| `EnqueueProjectDocumentation(projectId, userId, notifyUserId)` | AI documentation. Returns your runner's job id. Throws until implemented. |
| `EnqueueAiActorThinkCycle(actorId, subscriptionId)` | AI actors. Returns a job id. Throws until implemented. |
| `EnqueueMcpEval(runId)` | MCP evaluation runs. Returns a job id. Throws until implemented. |
| `EnqueueMetadataRefresh(dataSourceId)` | Re-scan after a data source was pointed at another target. Optional: the default returns `null` and Beacon logs that the old schema stays until a refresh. |
| `EnqueueBackgroundTask(taskId, notifyUserId)` | A task a user started and follows (a background metadata refresh); your job calls `IBackgroundTaskService.ExecuteAsync(taskId, ct)`. Optional: the default returns `null` and Beacon runs the task in the request. |

`cron` is a standard 5-field expression, already validated by Beacon. Key recurring jobs by the entity id, never by the name.

Every job opens its own DI scope, marks it as system work with `ISystemWorkScope.MarkAsSystemWork()` **before** it calls Beacon, and calls one `IJobService` method. A scope that was not marked gets no project access and logs a warning. Work done for a user, such as a subscription's run, is still executed as that user: Beacon sets the owner itself ([the system-work rule](/integrate/background-jobs/#the-system-work-rule)). Do not resolve the current user in your wrapper.

```csharp
using Beacon.Core.Authorization;
using Beacon.Core.Worker;
using Hangfire;

[AutomaticRetry(Attempts = 0)]   // a failed check is investigated; a retry would notify twice
public sealed class BeaconHangfireJobs(ISystemWorkScope systemWork, IJobService jobs)
{
    public Task ExecuteSubscription(int subscriptionId, CancellationToken ct)
    {
        systemWork.MarkAsSystemWork();
        return jobs.ExecuteQuery(subscriptionId, ct);
    }

    public Task EvaluateContract(int contractId, CancellationToken ct)
    {
        systemWork.MarkAsSystemWork();
        return jobs.EvaluateDataContract(contractId, ct);
    }

    public Task RunMcpEval(int runId, CancellationToken ct)
    {
        systemWork.MarkAsSystemWork();
        return jobs.RunMcpEval(runId, ct);
    }

    public Task CleanupOldSignals(CancellationToken ct)
    {
        systemWork.MarkAsSystemWork();
        return jobs.CleanupOldSignals(ct);
    }

    // AggregateLearnedPatterns, ReindexEmbeddings and ReindexDocChunks follow the same shape.
}

public sealed class HangfireBeaconScheduler(IRecurringJobManager recurring, IBackgroundJobClient background) : IBeaconScheduler
{
    public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron)
    {
        recurring.AddOrUpdate<BeaconHangfireJobs>($"subscription-{subscriptionId}", x => x.ExecuteSubscription(subscriptionId, CancellationToken.None), cron);
        return Task.CompletedTask;
    }

    public Task Remove(int subscriptionId, string subscriptionName)
    {
        recurring.RemoveIfExists($"subscription-{subscriptionId}");
        return Task.CompletedTask;
    }

    public Task AddOrUpdateDataQualityJob(int contractId, string contractName, string cron)
    {
        recurring.AddOrUpdate<BeaconHangfireJobs>($"dq-{contractId}", x => x.EvaluateContract(contractId, CancellationToken.None), cron);
        return Task.CompletedTask;
    }

    public Task RemoveDataQualityJob(int contractId, string contractName)
    {
        recurring.RemoveIfExists($"dq-{contractId}");
        return Task.CompletedTask;
    }

    public Task<string> EnqueueMcpEval(int runId)
        => Task.FromResult(background.Enqueue<BeaconHangfireJobs>(x => x.RunMcpEval(runId, CancellationToken.None)));
}
```

Hangfire's ASP.NET Core integration creates one DI scope per job and builds `BeaconHangfireJobs` in it, so the `ISystemWorkScope` the wrapper marks is the one `IJobService` reads. Hangfire fills the `CancellationToken` argument with its shutdown token at run time. The Hangfire calls above (`AddOrUpdate<T>`, `RemoveIfExists`, `Enqueue<T>`, `[AutomaticRetry]`) come from Hangfire, not from Beacon: check them against the Hangfire version you install. For AI documentation and AI actors, add wrapper methods that call `IProjectDocumentationService.GenerateDocumentationAsync(projectId, userId, ct)` and `AiActorService.ExecuteThinkCycleBackgroundAsync(actorId, subscriptionId)` ([Background jobs](/integrate/background-jobs/#reference-implementation-moberg-warp) names their namespaces).

## 4. Register the maintenance jobs

Beacon expects these recurring jobs. Register each at startup, after `app.Build()`, with `IRecurringJobManager`:

| Id | Cron | Calls |
|---|---|---|
| `mcp-learning-aggregate` | `0 */6 * * *` | `IJobService.AggregateLearnedPatterns` |
| `mcp-learning-cleanup` | `0 3 * * *` | `IJobService.CleanupOldSignals` |
| `mcp-audit-retention` | `30 3 * * *` | `IMcpAuditRetentionService.PurgeExpiredAsync` |
| `security-audit-retention` | `45 3 * * *` | `ISecurityAuditRetentionService.PurgeExpiredAsync` |
| `security-audit-verify` | `15 * * * *` | `ISecurityAuditVerificationService.VerifyNewEventsAsync` |
| `mcp-embedding-reindex` | `0 */12 * * *` | `IJobService.ReindexEmbeddings` |
| `mcp-docchunk-reindex` | `0 */12 * * *` | `IJobService.ReindexDocChunks` |

```csharp
var recurring = app.Services.GetRequiredService<IRecurringJobManager>();
recurring.AddOrUpdate<BeaconHangfireJobs>("mcp-learning-cleanup", x => x.CleanupOldSignals(CancellationToken.None), "0 3 * * *");
// ... one line per row of the table, each with its own wrapper method ...
```

While `Beacon:PreviousEncryptionKeys` lists a retired key, also run `IStoredValueReEncryptionService.ReEncryptAsync` once per start ([Background jobs](/integrate/background-jobs/#recurring-maintenance-jobs)). The Warp dashboard and the live `JobStatusChanged` progress push are features of the reference host's Warp setup; with Hangfire the UI still works, it just shows no live progress for documentation jobs.

## 5. Create the schema in SQL Server

Beacon does not migrate itself. For one instance, call this after `builder.Build()`, as in the other recipe:

```csharp
Beacon.Core.ServiceConfiguration.UseBeacon(app.Services, createSchema: true);
```

It creates the `beacon` schema when it is missing and applies the pending migrations. The login needs the right to create the schema and the tables in it. If it cannot, create the `beacon` schema as an administrator and call `UseBeacon(app.Services)` without `createSchema`.

With several instances, or a separate migrator process that does not compose Beacon, call the standalone migrator once from a deployment step:

```csharp
await Beacon.Core.SqlServer.BeaconSqlServerMigrator.MigrateAsync(connectionString);
```

It creates the schema and applies the migrations, and it is idempotent. Both paths write the history table `beacon.__EFMigrationsHistory`. SQL Server needs no extension.

## 6. Know what SQL Server does without pgvector

On PostgreSQL, Beacon keeps embeddings in a `vector(384)` column with an HNSW index. On SQL Server there is no such column: Beacon stores the embedding bytes and computes similarity over them in memory. The embedding and document re-index jobs still run. As on PostgreSQL, retrieval is semantic only when you add `Moberg.Beacon.AI.Embeddings.Onnx` and call `AddBeaconOnnxEmbeddings()`; without it retrieval stays keyword-based. With it, SQL Server has no vector index to lean on, so the similarity work grows with the number of indexed items.

## 7. Monitoring SQL Server: the read-only login

For a data source on SQL Server, create a login that can only read. Beacon's SQL Server path relies on its SQL checks and this login, because SQL Server has no read-only session ([Data sources](/features/data-sources/)):

```sql
CREATE LOGIN beacon_reader WITH PASSWORD = '…';
USE YourDatabase;
CREATE USER beacon_reader FOR LOGIN beacon_reader;
ALTER ROLE db_datareader ADD MEMBER beacon_reader;
```

The connection string must verify the server: `Encrypt` Mandatory or Strict and no `TrustServerCertificate`, for example `Server=db.example.com;Database=YourDatabase;User Id=beacon_reader;Password=…;Encrypt=True;TrustServerCertificate=False`. Beacon refuses a connection that does not ([Transport security](/features/data-sources/#transport-security-tls)).

## Check it works

- [ ] The app starts. No `Implementation of IBeaconScheduler is required.`, no encryption-key error.
- [ ] In SQL Server, the `beacon` schema holds Beacon's tables and `beacon.__EFMigrationsHistory` lists the migrations.
- [ ] `/beacon/setup`, the first admin and `curl -k https://localhost:<port>/beacon/api/health` behave as in the other recipe.
- [ ] The Hangfire tables exist (schema `HangFire`) and the Hangfire server is running.
- [ ] After you create a subscription, `subscription-<id>` is in the Hangfire recurring jobs. After you delete it, the entry is gone.
- [ ] Create a subscription with a short schedule and wait for its run: the Hangfire job succeeds, with no "not marked as system work" warning in the log.

## If something goes wrong

| Symptom | Cause and fix |
|---|---|
| A warning that a scope is not marked as system work, and the job does nothing | The job did not call `MarkAsSystemWork()` before Beacon code. Check that every wrapper method does. |
| `NotImplementedException` mentioning scheduling or background enqueue | You use a feature (data contracts, AI documentation, AI actors, MCP evals) whose `IBeaconScheduler` member you have not implemented. |
| A failed check notifies twice | Hangfire retried it. Keep `[AutomaticRetry(Attempts = 0)]` on the wrapper class. |
| The migration fails with a permission error | The login cannot create the schema or tables. Create the `beacon` schema as an administrator and call `UseBeacon` without `createSchema`. |
| Startup fails on `Beacon:Schema` | The schema is fixed at `beacon`. Remove the key. |
| Jobs run twice with several instances | Hangfire stores recurring jobs in its database and fires each once across servers. Check that every instance uses the same Hangfire storage. |

## Next

- [Embed Beacon in an existing ASP.NET Core app](/cookbooks/embed-postgresql-warp/): the steps this recipe leaves out.
- [Background jobs](/integrate/background-jobs/): the scheduler contract and every job Beacon expects.
- [Metadata database](/integrate/database/): both providers, migrations and generating SQL scripts.
- [Production checklist](/integrate/production/): proxy, TLS, key rotation and backups.
