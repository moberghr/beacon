---
title: Embed Beacon in an existing ASP.NET Core app (PostgreSQL + Warp)
description: "Beacon's UI, API and MCP server running inside your ASP.NET Core application, storing its data in PostgreSQL and running its jobs on Moberg Warp, with a first admin and one monitored data source."
---

| | |
|---|---|
| **You end up with** | Your ASP.NET Core app serving Beacon at `/beacon` (UI, REST API, MCP server), Beacon's schema created in PostgreSQL, scheduled jobs running on Warp, a first admin, and one data source added with a read-only login. Your own routes stay as they are. |
| **Time** | about 45 minutes |
| **You need** | An ASP.NET Core app on .NET 10, a PostgreSQL server with the [pgvector](https://github.com/pgvector/pgvector) extension available and an empty database for Beacon, and the right to create a read-only login on the database you want to monitor. Read [Integrate Beacon](/integrate/) first if you have not. |
| **Applies to** | Beacon 5.0 · Full Beacon |

:::tip[Let your AI do it]
Paste this into Claude Code or another coding agent, in the repository of your app.

```text wrap
Read https://moberghr.github.io/beacon/cookbooks/embed-postgresql-warp.md and follow it step by step in this repository.
Ask me before every decision it marks as one, and show me the result of each "Check it works" item.
Never write the connection string, the encryption key or the setup token into a file that is committed: use
user-secrets or environment variables. Do not run the app against a database I have not named.
```
:::

## Before you start

| Decision | Options | Default |
|---|---|---|
| Job runner | Moberg Warp (this recipe), or another runner: see [Embed Beacon on SQL Server with Hangfire](/cookbooks/embed-sqlserver-hangfire/) | Warp |
| Warp's database | The Beacon database, or a separate PostgreSQL database (`ConnectionStrings:WarpContext`) | The Beacon database |
| Who signs in | Beacon's own user store (this recipe), or your app's sign-in: see [Sign-in & identity](/integrate/authentication/#your-app-owns-sign-in) | Beacon's user store |
| Connectors | One per engine you monitor; this recipe adds PostgreSQL | PostgreSQL |

## 1. Add the packages

```bash
dotnet add package Moberg.Beacon.Core.PostgreSql
dotnet add package Moberg.Beacon.Api
dotnet add package Moberg.Beacon.UI
dotnet add package Moberg.Beacon.AI
dotnet add package Moberg.Beacon.MCP
dotnet add package Moberg.Beacon.Connector.PostgreSql
dotnet add package Moberg.Warp.Core --version 3.11.0
dotnet add package Moberg.Warp.Worker --version 3.11.0
dotnet add package Moberg.Warp.Provider.PostgreSql --version 3.11.0
dotnet add package EFCore.NamingConventions
dotnet add package Microsoft.EntityFrameworkCore.Design
```

`Moberg.Beacon.Core` comes with the others. Keep every `Moberg.Beacon.*` package on one version ([Packages](/integrate/packages/#versions)). Drop `Moberg.Beacon.AI` and `Moberg.Beacon.MCP` if you want neither; [Host wiring](/integrate/host-setup/#minimal-variants) lists what else goes.

## 2. Set the two secrets

```bash
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:BeaconContext" "Host=localhost;Database=beacon;Username=beacon;Password=…"
dotnet user-secrets set "Beacon:EncryptionKey" "$(openssl rand -base64 32)"
```

Create the database first (`CREATE DATABASE beacon;`). Beacon creates the schema and tables in it, not the database. Outside development the same two keys come from your secret store or from `ConnectionStrings__BeaconContext` and `Beacon__EncryptionKey`. Beacon refuses to start with a missing, short or non-random key. Keep the key: stored connection strings cannot be read without it.

## 3. Write the Warp side

Beacon runs no jobs itself. Your host gives it an `IBeaconScheduler`, and each job marks its scope as system work before Beacon code runs. An unmarked job scope gets no project access and logs a warning ([the system-work rule](/integrate/background-jobs/#the-system-work-rule)).

`WarpDbContext.cs` holds Warp's own tables (the `warp` schema), apart from Beacon's:

```csharp
using Microsoft.EntityFrameworkCore;

public sealed class WarpDbContext(DbContextOptions<WarpDbContext> options) : DbContext(options);
```

`BeaconWarp.cs` registers Warp, applies its migrations and registers the recurring maintenance jobs:

```csharp
using Beacon.Core.Authorization;
using Beacon.Core.Worker;
using Microsoft.EntityFrameworkCore;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Handlers;
using Warp.Provider.PostgreSql;
using Warp.Worker;

public static class BeaconWarp
{
    public static IServiceCollection AddBeaconWarp(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<WarpDbContext>(x => x
            .UseNpgsql(configuration.GetConnectionString("WarpContext") ?? configuration.GetConnectionString("BeaconContext"))
            .UseSnakeCaseNamingConvention());
        services.AddWarpServer<WarpDbContext>(x =>
        {
            x.UsePostgreSql();
            x.PollingInterval = TimeSpan.FromSeconds(1);
            x.WorkerCount = Math.Min(Environment.ProcessorCount * 2, 20);   // protects the connection pool
            // No x.AddRetry(): a failed check is investigated, not retried and notified twice.
        });
        return services;
    }

    public static async Task StartBeaconWarpAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<WarpDbContext>().Database.Migrate();
        var recurring = scope.ServiceProvider.GetRequiredService<IRecurringJobPublisher>();
        await recurring.AddOrUpdateRecurringJob(new CleanupOldSignalsJob(), "mcp-learning-cleanup", "0 3 * * *");
        // ... one line per job in the table below ...
    }
}

// Warp registers this behaviour for every job. It is what marks the job's scope as system work.
public sealed class SystemWorkBehavior<TRequest, TResponse>(ISystemWorkScope systemWork) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public Task<TResponse> HandleAsync(TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken)
    {
        systemWork.MarkAsSystemWork();
        return next(request, cancellationToken);
    }
}

public sealed class ExecuteSubscriptionQueryJob : IJob { public int SubscriptionId { get; init; } }
public sealed class ExecuteSubscriptionQueryJobHandler(IJobService jobs) : IJobHandler<ExecuteSubscriptionQueryJob>
{
    public Task HandleAsync(ExecuteSubscriptionQueryJob message, CancellationToken ct) => jobs.ExecuteQuery(message.SubscriptionId, ct);
}

public sealed class EvaluateDataContractJob : IJob { public int ContractId { get; init; } }
public sealed class EvaluateDataContractJobHandler(IJobService jobs) : IJobHandler<EvaluateDataContractJob>
{
    public Task HandleAsync(EvaluateDataContractJob message, CancellationToken ct) => jobs.EvaluateDataContract(message.ContractId, ct);
}

public sealed class CleanupOldSignalsJob : IJob;
public sealed class CleanupOldSignalsJobHandler(IJobService jobs) : IJobHandler<CleanupOldSignalsJob>
{
    public Task HandleAsync(CleanupOldSignalsJob message, CancellationToken ct) => jobs.CleanupOldSignals(ct);
}

public sealed class BeaconScheduler(IRecurringJobPublisher recurring, WarpDbContext warp) : IBeaconScheduler
{
    public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron)
        => recurring.AddOrUpdateRecurringJob(new ExecuteSubscriptionQueryJob { SubscriptionId = subscriptionId }, $"subscription-{subscriptionId}", cron);

    public Task Remove(int subscriptionId, string subscriptionName) => RemoveRecurring($"subscription-{subscriptionId}");

    public Task AddOrUpdateDataQualityJob(int contractId, string contractName, string cron)
        => recurring.AddOrUpdateRecurringJob(new EvaluateDataContractJob { ContractId = contractId }, $"dq-{contractId}", cron);

    public Task RemoveDataQualityJob(int contractId, string contractName) => RemoveRecurring($"dq-{contractId}");

    private async Task RemoveRecurring(string name)
    {
        var job = await warp.Set<RecurringJob>().Where(x => x.Name == name).FirstOrDefaultAsync();
        if (job != null)
        {
            warp.Set<RecurringJob>().Remove(job);
            await warp.SaveChangesAsync();
        }
    }
}
```

Recurring jobs are keyed by the entity id, never by the name. Five of the other `IBeaconScheduler` members (`AddOrUpdateDataQualityJob`, `RemoveDataQualityJob`, `EnqueueProjectDocumentation`, `EnqueueAiActorThinkCycle`, `EnqueueMcpEval`) have default bodies that throw `NotImplementedException` until you implement them, and `EnqueueMetadataRefresh` defaults to doing nothing. Implement the ones for the features you use.

Beacon also expects these recurring maintenance jobs. `mcp-learning-cleanup` is in the code above. Write one `IJob` plus handler for each of the others, in the same shape, and register each in `StartBeaconWarpAsync` with `recurring.AddOrUpdateRecurringJob(new XJob(), id, cron)` (`recurring` is the scope's `IRecurringJobPublisher`). The handler code is in [Background jobs](/integrate/background-jobs/#reference-implementation-moberg-warp).

| Id | Cron | Handler calls |
|---|---|---|
| `mcp-learning-aggregate` | `0 */6 * * *` | `IJobService.AggregateLearnedPatterns` |
| `mcp-learning-cleanup` | `0 3 * * *` | `IJobService.CleanupOldSignals` |
| `mcp-audit-retention` | `30 3 * * *` | `IMcpAuditRetentionService.PurgeExpiredAsync` |
| `security-audit-retention` | `45 3 * * *` | `ISecurityAuditRetentionService.PurgeExpiredAsync` |
| `security-audit-verify` | `15 * * * *` | `ISecurityAuditVerificationService.VerifyNewEventsAsync` |
| `mcp-embedding-reindex` | `0 */12 * * *` | `IJobService.ReindexEmbeddings` |
| `mcp-docchunk-reindex` | `0 */12 * * *` | `IJobService.ReindexDocChunks` |

Create Warp's initial migration once: `dotnet ef migrations add WarpInitial --context WarpDbContext`.

## 4. Register Beacon and map its endpoints

`BeaconClaimsTransformation` is a small class you copy from [Host wiring](/integrate/host-setup/#the-claims-transformation): it adds the role claim from Beacon's user store. Then `Program.cs`:

```csharp
using Beacon.AI;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Connector.PostgreSql;
using Beacon.Core;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.PostgreSql;
using Beacon.MCP;
using Beacon.MCP.Discovery;
using Beacon.UI;

var builder = WebApplication.CreateBuilder(args);
const string beaconBasePath = BeaconUiEndpointRouteBuilderExtensions.BasePath;   // "/beacon"

// ... your own services stay here ...

builder.Services.AddBeaconWarp(builder.Configuration);
builder.Services.AddBeaconHostInfrastructure<BeaconClaimsTransformation>();

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.BaseUrl = builder.Configuration["Beacon:BaseUrl"];   // public URL including /beacon, for links in notifications
        options.UseAI = true;
        options.Authorization.Enabled = true;
        options.Authentication.EnableLoginForm = true;
        options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();
        options.EnableUserManagement();
    })
    .AddPostgreSqlConnector()
    .UsePostgreSql();                       // reads ConnectionStrings:BeaconContext; must end the chain

builder.Services.AddBeaconCookieAuthentication(beaconBasePath);
builder.Services.AddBeaconDataProtection(builder.Configuration, builder.Environment);
builder.Services.AddBeaconOidcAuthentication(builder.Configuration);   // does nothing unless Oidc:Enabled
builder.Services.AddBeaconAI(builder.Configuration);
builder.Services.AddBeaconMcp();
builder.Services.AddAntiforgery(x =>
{
    x.HeaderName = "X-XSRF-TOKEN";
    x.Cookie.Name = ".Beacon.Antiforgery";
});
builder.Services.AddBeaconApiAuthorization();
builder.Services.AddBeaconApiServices();

var app = builder.Build();
var beaconConfiguration = app.Services.GetRequiredService<BeaconConfiguration>();

Beacon.Core.ServiceConfiguration.UseBeacon(app.Services, createSchema: true);

// The order is load-bearing.
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseMcpResourceMetadataChallenge();
app.UseBeaconAuthentication();           // API key, bearer JWT, UseAuthentication, Beacon.Auth cookie
app.UseAuthorization();
app.UseMiddleware<LoginFormAuthMiddleware>(beaconConfiguration, beaconBasePath);
app.UseMiddleware<FirstRunSetupMiddleware>(beaconConfiguration, beaconBasePath);
app.UseAntiforgery();

app.MapBeaconApi();
app.MapLoginEndpoints(beaconBasePath, beaconConfiguration);
app.MapSetupEndpoints(beaconBasePath);
app.MapBeaconMcp();
app.MapMcpDiscovery();
app.MapBeaconUi();
// ... your own endpoints stay here ...

await app.StartBeaconWarpAsync();
app.Run();
```

What to know about this file:

- `UseBeacon(app.Services, createSchema: true)` is the schema step. It creates the `beacon` schema if missing and applies the pending migrations, synchronously, before the host serves a request. One migration runs `CREATE EXTENSION IF NOT EXISTS vector;`, so the login needs the right to create it, or an administrator installs it first ([pgvector](/integrate/database/#pgvector-on-postgresql)).
- With several instances, run migrations once from a deployment step instead: `await Beacon.Core.PostgreSql.BeaconPostgreSqlMigrator.MigrateAsync(connectionString);` in a migrator process that does not compose Beacon ([Metadata database](/integrate/database/#creating-and-migrating-the-schema)).
- Beacon's middlewares act on `/beacon` only. Your routes keep your authentication.
- Behind a proxy, add `UseForwardedHeaders` first ([Production checklist](/integrate/production/)).

## 5. Create the first admin

Start the app. While no admin exists, Beacon prints a one-time setup token to the process console (standard error, not the log):

```text wrap
Beacon first-run setup token (enter it on the setup page): 3f1c…
```

Open `https://localhost:<port>/beacon/setup`, enter the token, then a user name and password (at least 8 characters, with upper case, lower case, a digit and a special character), and click **Create super admin**. With several replicas set `Beacon:UserManagement:SetupToken` (at least 32 characters) from your secret store, because each replica generates its own. Details: [First-run setup](/security/user-management/#first-run-setup).

## 6. Add a data source with a read-only login

On the server you want to monitor, create a login that cannot write, for PostgreSQL:

```sql
CREATE ROLE beacon_reader LOGIN PASSWORD '…';
ALTER ROLE beacon_reader SET default_transaction_read_only = on;
GRANT USAGE ON SCHEMA public TO beacon_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO beacon_reader;
```

In Beacon, open **Data Sources**, click **New data source**, pick **Database**, choose the **Database engine**, paste the **Connection string**, click **Test connection** (wait for **Connected**), then **Create data source**. Outside development Beacon refuses a connection that does not verify the server's certificate, for PostgreSQL `SSL Mode=VerifyFull` ([Transport security](/features/data-sources/#transport-security-tls)).

## Check it works

- [ ] The app starts without `Implementation of IBeaconScheduler is required.` or an encryption-key error.
- [ ] `psql` shows the `beacon` schema with tables, and `\dx` shows `vector`.
- [ ] `https://localhost:<port>/beacon/` redirects to `/beacon/setup`, and after step 5 to `/beacon/login`. Your own routes still answer as before.
- [ ] `curl -k https://localhost:<port>/beacon/api/health` returns `{"status":"ok"}`, also before the first admin exists. A `503` means Beacon cannot reach its database.
- [ ] The data source shows **Connected** and its tables appear after the metadata load.
- [ ] The `warp` schema exists and `warp.recurring_job` lists the maintenance jobs you registered.
- [ ] After you create a subscription, `warp.recurring_job` lists `subscription-<id>`.

## If something goes wrong

| Symptom | Cause and fix |
|---|---|
| `Implementation of IBeaconScheduler is required.` | `options.AddBeaconScheduler<BeaconScheduler>()` is missing from the `AddBeaconServices` lambda. |
| Startup refuses the encryption key | It must be base64 of at least 32 random bytes: `openssl rand -base64 32`. A `${…}` placeholder that was never expanded fails too. |
| `extension "vector" is not available` | Install pgvector on the server, or on Azure Database for PostgreSQL add `VECTOR` to `azure.extensions`. |
| Startup fails outside Development about Data Protection | Set `Beacon:DataProtection:ApplicationName` and chain `ProtectKeysWithCertificate(...)` or `ProtectKeysWithAzureKeyVault(...)`, or configure your own Data Protection and drop `AddBeaconDataProtection` ([Host wiring](/integrate/host-setup/#data-protection)). |
| Warning that a scope is not marked as system work, and jobs do nothing | A job ran without `ISystemWorkScope.MarkAsSystemWork()`. Check that `SystemWorkBehavior` is compiled into your host project, which is where Warp's source generator registers it. |
| `CS9137` when you add `AddOpenApi()` | Reference `Microsoft.AspNetCore.OpenApi` directly ([Packages](/integrate/packages/#build-time-notes)). |
| The data source is refused for its connection string | The connection does not verify the server over TLS. Fix the connection string; do not look for a way to turn the check off. |

## Next

- [Host wiring](/integrate/host-setup/): every option of `AddBeaconServices`, the middleware table and the routes.
- [Sign in to Beacon with Microsoft Entra ID](/cookbooks/entra-sso/): single sign-on for the UI.
- [Production checklist](/integrate/production/): proxy, TLS, key rotation, backups.
- [MCP gateway for your application](/mcp/gateway/): if you want AI clients on your own endpoints, not on data sources.
