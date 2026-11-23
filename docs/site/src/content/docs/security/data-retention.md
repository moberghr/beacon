---
title: Data retention and content lock
description: "How long Beacon keeps run history, logs and the content of past queries, how content is cleared before rows are deleted, and how a deployment can forbid keeping anything forever."
---

Beacon stores what happened: subscription runs, notifications, AI actor output, migration runs, the execution log, MCP audit rows and learning signals. Some of it holds **content**: SQL text, result rows, questions and error text from your databases. Keeping content longer than needed is a privacy risk, and keeping nothing at all breaks audit.

This page explains what is stored where, which setting purges each store, how the **content lock** keeps content out of storage, and how a host schedules the purge. Read it when you set retention windows for the first time, when an auditor asks how long data lives, or when a host fails to start under `Beacon:Retention:Locked`.

## What is kept, and for how long

By default Beacon deletes nothing. A window of `0`, or no window, means "keep forever", so an upgrade never deletes data you did not ask it to delete. You opt in per store.

| Store | What it holds | Purged by | Default |
|---|---|---|---|
| Subscription runs (`subscription-runs`) | Run history: the SQL that ran, the stored result snapshot | `Beacon:Retention` | forever |
| Notifications (`notifications`) | A delivered copy per recipient, with its results | `Beacon:Retention` | forever |
| Anomaly baselines (`anomaly-baselines`) | Data points the anomaly detection compares with | `Beacon:Retention` | forever |
| Anomaly events (`anomaly-events`) | Detected anomaly events | `Beacon:Retention` | forever |
| AI actor executions (`ai-actor-executions`) | What an AI actor decided, did and found, and its conversation turns | `Beacon:Retention` | forever |
| AI actor plans (`ai-actor-plans`) | Decided plans (not pending or executing) | `Beacon:Retention` | forever |
| Migration runs (`migration-runs`) | Finished data migration runs | `Beacon:Retention` | forever |
| MCP eval runs (`mcp-eval-runs`) | Finished eval runs with per-case results | `Beacon:Retention` | forever |
| Execution log (`execution-log`) | Statements Beacon ran for its own features | `Beacon:Retention` target, or `Beacon:Audit:ExecutionLog:RetentionDays` | forever |
| MCP audit | One row per MCP tool call, with its statements | `Beacon:Mcp:Audit:RetentionDays` | forever |
| Security audit trail | Sign-ins, role and settings changes, approvals | `Beacon:Audit:RetentionDays` | forever |
| MCP learning signals | What the learning system learned from calls | Per-setting `LearningSignalRetentionDays` | 90 days |
| Settings history | Every change of the admin and MCP settings | Never purged | forever |

The first nine rows (all but the MCP audit, the security audit trail, the learning signals and the settings history) are the **retention targets**: one job, `IDataRetentionService`, purges them all. The other rows have their own purge, described in [Purges that are not retention targets](#purges-that-are-not-retention-targets).

Never purged by any job: configuration and governance records (projects, data sources, metadata, queries, subscriptions, tasks, glossary, golden eval cases, learned patterns) and the query step change history.

## `Beacon:Retention`

The windows are read once at startup. Nothing changes them at runtime.

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:Retention:Days` | int | `0` (keep forever) | 0 to 36500 | Days the rows of every target are kept. `90` is recommended. |
| `Beacon:Retention:Targets:{name}` | int | the value of `Days` | 0 to 36500 | A window for one target, replacing `Days` for it. `0` keeps that target forever. |
| `Beacon:Retention:ContentDays` | int | `0` (off) | 0 to 36500 | Days the content of the content stores is kept. The row stays until its target's window. `30` is recommended for regulated deployments. |
| `Beacon:Retention:Locked` | bool | `false` | | The deployment lock, see [Locked retention](#locked-retention). |

The run time budget (`RunBudgetMinutes`) is fixed at 50 minutes, not a setting. See [Scheduling](#scheduling).

### Valid target names

A name in `Targets` that is not registered fails startup. These are the names Beacon registers, in the order the job runs them:

| Name | Notes |
|---|---|
| `subscription-runs` | Keeps each subscription's newest non-skipped run whatever its age: it is the baseline the next trigger compares with. |
| `notifications` | |
| `anomaly-baselines` | The cutoff moves back by the longest configured anomaly look-back. |
| `anomaly-events` | Same look-back rule. |
| `ai-actor-executions` | Keeps an execution that a query change or a newer turn still references. |
| `ai-actor-plans` | Runs after executions. Keeps a plan an execution or a query change still names, or that a revision names as its parent. |
| `migration-runs` | Only finished runs. A run that a retry names as its parent stays until the retry goes. |
| `mcp-eval-runs` | Only finished runs. The golden cases are never purged. |
| `execution-log` | Statements run by Beacon features. |

A component or host can register another target (`IRetentionTarget`), and its name then becomes a valid key.

### Examples

Keep everything (the default):

```json
{ "Beacon": { "Retention": { "Days": 0 } } }
```

Rows for 90 days, content for 30 days:

```json
{
  "Beacon": {
    "Retention": {
      "Days": 90,
      "ContentDays": 30
    }
  }
}
```

A different window for two targets:

```json
{
  "Beacon": {
    "Retention": {
      "Days": 90,
      "ContentDays": 30,
      "Targets": {
        "notifications": 30,
        "execution-log": 365
      }
    }
  }
}
```

The environment-variable form:

```bash
Beacon__Retention__Days=90
Beacon__Retention__ContentDays=30
Beacon__Retention__Targets__notifications=30
```

A value outside the range, or a name that is not a target, fails startup:

```text
Beacon:Retention:Days must be between 0 (keep forever) and 36500 days.
Beacon:Retention:Targets:subscription-run names no retention target; known targets: ai-actor-executions, ai-actor-plans, anomaly-baselines, anomaly-events, execution-log, mcp-eval-runs, migration-runs, notifications, subscription-runs.
```

## Content clearing and row deletion

A run does two different things, in this order.

1. **Content clearing.** For rows older than `ContentDays`, every content property becomes empty, and error text becomes its class. The row stays.
2. **Row deletion.** For each target with a window above zero, rows older than the window are deleted, in batches, never the rows another store still references.

Clearing first means a long row window can keep evidence that something ran, without keeping what it read.

### Which properties are content

`McpRetentionDenyList` classifies every string property of every `Mcp*` entity and of the **content stores** outside MCP. Each property is one of three kinds:

| Kind | What the lock and the expiry do |
|---|---|
| Content | Nulled (an empty string where the column is required). SQL text, questions, result snapshots, model output, feedback notes. |
| Error class | Free-text error replaced by its class. |
| Structural | Always kept: names, flags, timings, hashes, configuration. |

The content stores outside MCP (`ContentStores`) are `QueryExecutionHistory`, `Notification`, `ManualQueryExecutionLog`, `AiActorExecution`, `AiActorConversation` and `MigrationExecutionHistory`. A test fails when a string property is not classified, so a new store cannot ship unclassified.

### Error text becomes its class

The error text of a database or provider can contain table names, values and user input. Clearing replaces it with one word from a fixed vocabulary: `schema`, `syntax`, `permission`, `timeout`, `not_found`, `validation`, `execution`, `cancelled` or `unknown`. The first matching class wins, in this order: timeout, cancelled, permission, syntax, schema, not found, validation, execution. A value already in the vocabulary is left alone.

An execution log row, before and after the content is cleared:

| Column | Before | After |
|---|---|---|
| `QueryText` | `SELECT email FROM customers WHERE id = 4711` | empty string (required column) |
| `ErrorMessage` | `ERROR: relation "orders" does not exist` | `schema` |
| `ExecutionContext` | `preview` | `preview` (structure, kept) |
| `CreatedTime`, `UserId` | unchanged | unchanged |

A subscription run: `CompiledSql` becomes empty, `Results` becomes null, and `Comment` (structure) stays. An MCP audit row: `Parameters` becomes a structural JSON with the tool, the size of the input and the tables touched, for example `{"tool":"query","params":[{"name":"input","bytes":182}],"tables":["orders"]}`. Its `ContentHash` stays, so identical calls still match.

The execution log is sealed. A row whose seal verifies is cleared and sealed again in the same statement. A row whose seal does not verify is cleared without a new seal, so it keeps failing verification. See [Audit trail](/security/audit-trail/).

## The content lock

The content lock means Beacon never writes content to storage.

It is on when the effective MCP setting `RetainQueryContent` is off:

- globally or per project, in the Admin settings (`RetainQueryContent` defaults to on), or
- for the whole deployment, with `Beacon:Mcp:ForceNoContentRetention: true`, which wins over every project (see [Deployment locks](/security/deployment-locks/)).

While it is on:

- `ContentRetentionInterceptor` strips content-classified properties from every added or modified `Mcp*` row of a locked project, and from every content store outside MCP under the **global** decision (their rows carry no single project).
- It never touches structural properties.
- If the settings cannot be resolved, it fails closed and treats the row as locked.
- The retention job clears every stored result snapshot (subscription run results and notifications) whatever its age, in addition to `ContentDays`.
- Explicit feedback content (a corrected SQL, a note) is a subset of query content, so it is not kept either.

The opposite lock, `Beacon:Mcp:Audit:RequireContent`, pins content retention on. The two cannot both be on.

## Locked retention

`Beacon:Retention:Locked: true` forbids keep-forever windows. With it on, these must all be greater than zero, or the host fails to start:

- `Beacon:Retention:Days` (also when every target sets its own window, because it caps the learning-signal window),
- every target's own window, or `Days` for those without one,
- `Beacon:Retention:ContentDays`,
- `Beacon:Mcp:Audit:RetentionDays`.

Each missing window is one failure line:

```text
Beacon:Retention:Locked is on, so Beacon:Retention:Days must be greater than zero: no run history or log may be kept forever (outside the lock by design: the security audit trail (its own Beacon:Audit:RetentionDays), learning signals (their own window, capped by Days), the query step change history, settings history, and configuration: projects, data sources, metadata, queries, subscriptions, tasks, glossary, golden eval cases and learned patterns).
Beacon:Retention:Locked is on, so Beacon:Retention:ContentDays must be greater than zero: no run history or log may be kept forever (outside the lock by design: ...).
Beacon:Retention:Locked is on, so Beacon:Mcp:Audit:RetentionDays must be greater than zero: no run history or log may be kept forever (outside the lock by design: ...).
```

The lock also caps the one window Admins edit at runtime, the learning-signal retention: it never resolves to more than `Beacon:Retention:Days`.

Exempt stores, by design, because they are governance or configuration and not run history: the security audit trail (it has its own `Beacon:Audit:RetentionDays`), learning signals (their own window, capped by `Days`), the query step change history, the settings history and configuration records.

The [regulated profile](/security/regulated-profile/) turns `Locked` on when it is unset. It sets no windows itself.

A configuration that satisfies the lock:

```json
{
  "Beacon": {
    "Retention": {
      "Locked": true,
      "Days": 90,
      "ContentDays": 30,
      "Targets": { "execution-log": 365 }
    },
    "Mcp": { "Audit": { "RetentionDays": 365 } }
  }
}
```

## Purges that are not retention targets

Three stores have their own purge and their own key. They are separate from `IDataRetentionService`.

| Store | Key | Range | Default | Purge service | Sample host job |
|---|---|---|---|---|---|
| MCP audit | `Beacon:Mcp:Audit:RetentionDays` | 0 or more (`0` or unset = forever) | forever | `IMcpAuditRetentionService` | `mcp-audit-retention`, daily 03:30 |
| Security audit trail | `Beacon:Audit:RetentionDays` | 365 to 36500, or unset | forever | `ISecurityAuditRetentionService` | `security-audit-retention`, daily 03:45 |
| Execution log | `Beacon:Audit:ExecutionLog:RetentionDays` | greater than 0, or unset | forever | `IExecutionLogRetentionService` | `execution-log-retention`, daily 03:40 |

`365` is the recommended MCP audit window (`McpAuditOptions.RecommendedRetentionDays`). The security audit trail purge verifies every event it is about to delete and refuses when one fails verification: tampered evidence is never deleted. Its deletion and its `audit.purged` event commit in one transaction. See [Audit trail](/security/audit-trail/).

The execution log can be purged two ways: as the `execution-log` retention target and with its own `Beacon:Audit:ExecutionLog:RetentionDays`. Both delete rows older than their window, so the shorter one decides.

Learning signals are cleaned by the setting `LearningSignalRetentionDays` (default 90, a project may override it), through the sample host's `mcp-learning-cleanup` job at 03:00.

```json
{
  "Beacon": {
    "Audit": {
      "RetentionDays": 730,
      "ExecutionLog": { "RetentionDays": 365 }
    },
    "Mcp": { "Audit": { "RetentionDays": 365 } }
  }
}
```

## Scheduling

Beacon schedules none of these purges itself. The host must. If the job never runs, no data is deleted, whatever the windows say.

### One run

`IDataRetentionService.RunAsync` does one run:

- It works within 50 minutes (`RetentionOptions.RunBudgetMinutes`). Each step (content, then each target) gets an equal share of the time left. A step whose share runs out stops, and the next run continues it.
- A target whose effective window is `0` is skipped.
- A failing target is logged and counted without stopping the others. After all steps, the run throws, so the scheduled job fails visibly. Failed jobs are not retried by Beacon.
- It returns a `DataRetentionRun`: rows deleted per target, rows whose content was cleared, and the steps that were left incomplete.

Log events from the run:

| Event | Name | Level | Content |
|---|---|---|---|
| 9120 | `DataRetentionPurged` | Information | Rows deleted per target, cutoff, window in days |
| 9121 | `DataRetentionTargetFailed` | Error | Target name and failure code |
| 9122 | `DataRetentionContentCleared` | Information | Rows whose content was cleared, cutoff, `ContentDays`; or the number of snapshots cleared under the lock |
| 9124 | `DataRetentionBudgetSpent` | Information | A step left for the next run |
| 9123 | `RetentionUnconfigured` | Information | At startup, when no retention is configured at all |

An example line:

```text
Data retention deleted 1842 notifications rows older than <cutoff time> (retention 90 days)
```

### Warp (the sample host)

The sample host registers a recurring Warp job, `data-retention`, daily at 03:50, one run at a time, capped at one hour:

```csharp
[Timeout(DataRetentionJob.TimeoutSeconds, Mode = TimeoutMode.Fail)]
[Mutex(DataRetentionJob.Name)]
public sealed class DataRetentionJob : IJob
{
    public const string Name = "data-retention";
    public const int TimeoutSeconds = 3600;
}

public sealed class DataRetentionJobHandler(IDataRetentionService retentionService) : IJobHandler<DataRetentionJob>
{
    public Task HandleAsync(DataRetentionJob message, CancellationToken cancellationToken)
        => retentionService.RunAsync(cancellationToken);
}
```

```csharp
await recurringJobPublisher.AddOrUpdateRecurringJob(new DataRetentionJob(), DataRetentionJob.Name, "50 3 * * *");
```

### Another scheduler

`IDataRetentionService` is registered by `AddBeaconServices`. Call it from any scheduler, with a timeout longer than 50 minutes:

```csharp
// For example inside a Quartz job or a hosted timer.
public async Task Execute(CancellationToken cancellationToken)
{
    await using var scope = serviceProvider.CreateAsyncScope();
    var retention = scope.ServiceProvider.GetRequiredService<IDataRetentionService>();

    var run = await retention.RunAsync(cancellationToken);

    logger.LogInformation("Retention cleared content of {Cleared} rows", run.ContentCleared);
}
```

The same applies to `IMcpAuditRetentionService`, `ISecurityAuditRetentionService` and `IExecutionLogRetentionService` (each has `PurgeExpiredAsync`).

### Heartbeat and the startup warning

The MCP audit purge, the security audit purge and the security audit verification leave a heartbeat (`mcp-audit-retention`, `security-audit-retention`, `security-audit-verify`) when they run to their end. At startup `RetentionScheduleStartupCheck` warns when a job that should be running has no recent run:

- A purge is expected only while its retention key is set. It is expected within 36 hours. The verification is always expected, within 6 hours.
- A job with no recorded run is named only once the deployment's first security audit event is older than the job's window, so a new install is not warned.

```text
Maintenance job mcp-audit-retention last ran at 2026-11-20T03:30:02.0000000Z, longer ago than the 36 hours it is expected within: its schedule may be missing or stopped.
Maintenance job security-audit-retention has no recorded run (expected at least every 36 hours). Beacon does not schedule it: the host must (the sample host does with a Warp recurring job).
```

The `data-retention` job and the execution log purge leave no heartbeat, so this warning does not cover them. Watch for events 9120 and 9122 instead.

When no retention is configured at all, Beacon logs one Information at startup (event 9123) that names the keys and the recommended values: 90 days for rows, 365 for the MCP audit, 30 for content in regulated deployments.

## Recommended values

| Setting | Development | Production | Regulated |
|---|---|---|---|
| `Beacon:Retention:Days` | `0` | `90` | `90` (required above zero) |
| `Beacon:Retention:ContentDays` | `0` | `30` | `30` (required above zero) |
| `Beacon:Retention:Locked` | `false` | `false` or `true` | `true` (filled when unset) |
| `Beacon:Mcp:Audit:RetentionDays` | unset | `365` | `365` (required above zero under the lock) |
| `Beacon:Audit:RetentionDays` | unset | by policy, at least 365 | by policy, at least 365 |
| `Beacon:Audit:ExecutionLog:RetentionDays` | unset | `365` | `365` |
| `Beacon:Mcp:ForceNoContentRetention` | `false` | by decision | by decision (pair with `ContentDays`) |

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Startup fails with `Beacon:Retention:Locked is on, so ... must be greater than zero` | A window is `0` or unset under the lock. | Set every window named in the failure. |
| Startup fails with `names no retention target; known targets: ...` | A name under `Targets` is misspelled. | Use one of the names in [Valid target names](#valid-target-names). |
| Nothing is deleted | No window is set, or the job is not scheduled. | Set `Beacon:Retention:Days` and schedule `IDataRetentionService.RunAsync`. |
| A target is never purged | Its own window is `0`. | Set `Targets:{name}` above zero, or remove it to use `Days`. |
| The run throws `Data retention did not complete for: ...` | One target failed. | Read the Error event 9121 for each target. The other targets ran. |
| Event 9124 appears every run | The backlog is larger than the budget. | Nothing to fix. The next runs continue. Run the job daily. |
| A subscription's newest run is kept | It is the baseline for the next trigger. | By design. |
| Warning `Maintenance job ... has no recorded run` | The host does not schedule the job. | Schedule it, as in the sample host. |
| Content is still present after `ContentDays` | The job has not run since, or the row was created after the cutoff. | Check event 9122. |
| Stored SQL text is empty | The content lock is on for the project or the deployment. | By design. Check `RetainQueryContent` and `Beacon:Mcp:ForceNoContentRetention`. |

## Related pages

- [Deployment locks](/security/deployment-locks/)
- [Regulated profile](/security/regulated-profile/)
- [Audit trail](/security/audit-trail/)
- [MCP observability and audit](/mcp/observability-and-audit/)
- [Configuration reference: Data retention](/getting-started/configuration/#data-retention-beaconretention)
