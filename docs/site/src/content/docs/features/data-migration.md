---
title: Data Migration
description: Copy rows from a read source into a database marked as a migration write target, in Insert, Upsert or Truncate mode.
---

A migration job reads rows from a source through the same read-only path every Beacon query uses, then writes them into
a table of a destination database. It is the only Beacon feature that writes to a connected database, so it is off
unless the host turns it on, and it writes only into a data source an Admin marked for it.

What a job does:

- reads one source query (plain SQL, or a JSON list of steps across data sources) within its own row budget;
- writes the rows into one destination table in one of three modes: **Insert only**, **Insert or update** (Upsert) or
  **Truncate & insert**;
- records every run in its history: rows read, written and failed, duration and error.

Typical uses: copy reference data into an analytics database, keep a reporting table refreshed, move rows between
PostgreSQL, MySQL and SQL Server.

## Turning data migration on

Data migration is off by default. The host turns it on in configuration:

```json
{
  "Beacon": {
    "Migrations": {
      "Enabled": true,
      "MaxRows": 100000
    }
  }
}
```

| Key | Default | What it does |
|-----|---------|--------------|
| `Beacon:Migrations:Enabled` | `false` | Deployment lock. While `false`, nobody can create or run a job, an Admin included. |
| `Beacon:Migrations:MaxRows` | `100000` | Row budget of a migration's source read (1 to 10,000,000), used in place of `Beacon:Query:MaxExecutionRows`. |

While `Enabled` is `false`:

- the `/beacon/api/migrations/*` routes are not mapped (every one answers `404`), and the UI shows no **Data Migration**
  entry, page or Home card;
- the migration handlers refuse every request (`409`), also for an in-process caller that does not use the routes;
- no data source can be marked as a migration write target (`409`). An existing target can still be returned to a read
  source.

See [Configuration](/getting-started/configuration/#data-migration).

## Migration write targets

A job writes only into a data source an Admin marked as a **migration write target**: on the data source's page, choose
**Mark as migration write target**, or call `PUT /beacon/api/data-sources/{id}/migration-writes` (Admin only).

- Only a PostgreSQL, MySQL or SQL Server database can be a write target. A host-managed data source or one marked
  read-only cannot.
- The host registers the target engine's connector, which brings its writer: `AddPostgreSqlConnector()`
  (`Moberg.Beacon.Connector.PostgreSql`), `AddSqlServerConnector()` (`Moberg.Beacon.Connector.SqlServer`) or
  `AddMySqlConnector()` (`Moberg.Beacon.Connector.MySql`). Without it a run fails, naming the package, before the
  target's credentials are decrypted.
- Beacon never reads a write target. Query steps, previews, subscriptions, data quality, MCP tools, AI grounding,
  documentation and metadata scans refuse it or leave it out. Only the migration writer connects with its credentials, so
  only its login holds [write grants](#destination-privileges); every data source Beacon reads keeps a read-only login.
- A job's declared source and the data source of every step it runs may not be a write target. To copy within one
  database, register it twice: once with a read-only login (the source) and once with the write login (the target).
- Marking a data source that live queries, subscriptions or data contracts read is refused (`409`, with the count of
  each) unless the Admin confirms it (`"force": true`; the page asks a second time). Those readers fail once Beacon stops
  reading the source.
- Returning a target to a read source asks for confirmation: Beacon reads it again with its stored login, so change that
  login to a read-only one first.
- Every change is recorded in the settings history (`DataSource.{id}.AllowMigrationWrites`, before and after, and who)
  and as the `MigrationWritesChanged` security log event (7115, ids only).

The flag is checked when a job is created, before every run, and by the writer before it decrypts the destination's
credentials. A run started from the page or the API whose destination is not (or no longer) a write target, or that reads
one, is refused with the reason (`400`). A run refused inside the migration service itself (an in-process caller, or a
flag changed between the two checks) is recorded in the job's history as failed, with the reason.

See [Data Sources](/features/data-sources/) for the data source side.

## Who can use it

Data migration needs the **MigrationOperator** permission (an Admin holds every permission; see
[Security model](/security/)). Listing jobs, opening one, creating, running and deleting jobs and reading
the run history all need it. The **Data Migration** entry is hidden from other users.

A MigrationOperator gets no bypass of project access. A job reads its declared source and the data source of every step
in its query. Creating it, every run of it and deleting it need:

- **Query** on each of those data sources and **Write** on the destination, through the projects that link them
  (`404` for a data source the caller cannot read, `403` for one it reads but may not query or write);
- a destination that is **no more widely reachable** than any source: every live project that links the destination must
  link each source too, and an openly reachable destination needs openly reachable sources (`403` otherwise). Copying data
  never makes it readable to more people.

The job list, a job and the run history show a MigrationOperator only jobs whose source and destination they can read
(another job is not found, `404`). A failed run's error text, which can quote any data source the job reads, is replaced
by a note unless the caller reads every one of them. An Admin is not checked. Refusals are logged with ids only.

The Home page's migration summary shows Admins the totals of every job, MigrationOperators the totals of the jobs their
list shows, and zeros to everyone else.

## Create a job

1. Open **Data Migration** (`/beacon/migration-jobs`).
2. Click **New job**.
3. Fill in the form and click **Create migration job**.

| Field | Required | Default | What it does |
|-------|----------|---------|--------------|
| **Job name** | Yes | | Up to 200 characters. |
| **Description** | Yes | | Up to 1,000 characters. |
| **Source data source** | Yes | | The data source the job reads. Cannot be a write target. |
| **Source SQL** | Yes | | The source query. See [Source query](#source-query). |
| **Destination data source** | Yes | | Lists migration write targets only. |
| **Destination table** | Yes | | For example `public.target_table`. |
| **Migration mode** | Yes | Insert only | **Insert only**, **Insert or update** or **Truncate & insert**. See [Modes](#modes). Any other value is refused. |
| **Cron schedule** | No | empty | Empty, or a standard 5-field cron expression (anything else is refused). Stored with the job; Beacon does not run it. See [Schedules](#schedules). |
| Max retries (API only) | No | 3 | Not on the form. The API accepts 0 or 3 and refuses anything else; a failed run is never retried automatically. |
| **Timeout (minutes)** | No | 30 | 1 to 1,440 (anything else is refused). A run that lasts longer is stopped and recorded as failed. |
| **Enable migration job** | No | on | A disabled job does not run. |
| Validate before execution (API only) | No | on | Not on the form. Every run checks its source SQL and destination table before it writes; the API refuses `false`. |
| **Transformation script (optional)** | No | empty | Leave it empty. A job with a script fails when it runs. |

The job is saved when the form is valid; its SQL is not run at creation. The destination table is checked for existence
on every run.

:::note
A job cannot be edited after it is created; no route changes one. To change its query, destination, mode, schedule or
whether it is enabled, create a new job and delete the old one. The old job's run history is kept.
:::

### Source query

The source is read the way every query runs (see [Queries](/features/queries/)): through the read-only execution gate
and the source's read-only path, within `Beacon:Query:StatementTimeoutSeconds` (default 120 seconds) per statement. The
job's steps run as stored on the job, in the run itself: no query is saved to run them, so none is left behind.

- **Row budget.** `Beacon:Migrations:MaxRows` replaces `Beacon:Query:MaxExecutionRows`. The cap is written into the
  source SQL and the read stops one row past it. A larger source fails the run, with a message naming
  `Beacon:Migrations:MaxRows`, before anything is written. Split the job (for example by date or key range) or raise the
  budget. A run holds every row it copies in memory, so size the budget to the host.
- **Failed reads write nothing.** If any step or the final query fails or times out, the run is recorded as failed and the
  destination is not opened. A Truncate-mode job never empties its table on a failed read.
- **Columns map by name.** The source's column names are used as the destination column names.
- **Parameters.** A run from **Run now** or `POST /beacon/api/migrations/jobs/{id}/run` passes no parameter values.

**Several data sources in one job.** The job's query text can also be a JSON array of steps, each naming its own data
source. The form takes a single SQL statement; send the JSON form through `POST /beacon/api/migrations/jobs`:

```json
[
  { "Name": "Users",  "DataSourceId": 3, "SqlValue": "SELECT user_id, email FROM users WHERE active = true" },
  { "Name": "Orders", "DataSourceId": 7, "SqlValue": "SELECT user_id, COUNT(*) AS order_count FROM orders GROUP BY user_id" }
]
```

Each step is a read on its own data source, and the caller needs **Query** on each. Results of several data sources are
joined in an in-memory SQLite database, as for any multi-step query. Anything that is not a JSON array is read as one
step of plain SQL.

### Modes

| Mode (UI label) | What it does | Needs |
|-----------------|--------------|-------|
| **Insert only** | Inserts every source row. A row that violates a key or constraint fails the write. | |
| **Insert or update** (Upsert) | Inserts new rows and updates existing ones by primary key. | A primary key on the destination table, and every key column in each source row (otherwise the run fails). Without a primary key the run fails. |
| **Truncate & insert** | Empties the destination table, then inserts every source row. | |

:::caution
**Truncate & insert** deletes every row in the destination table on each run. Run the job by hand against a test table
before you enable it on real data.
:::

How the write is done per engine:

| Engine | Insert | Upsert |
|--------|--------|--------|
| PostgreSQL | `COPY … FROM STDIN (FORMAT BINARY)` | `COPY` into a temporary table, then `INSERT … ON CONFLICT` |
| SQL Server | bulk copy, keeping identity values | bulk copy into a session temporary table, then `MERGE` (a matched row is updated only when a value changed) |
| MySQL | multi-row parameterised `INSERT … VALUES (…), (…)` | the same, with `ON DUPLICATE KEY UPDATE col = VALUES(col)` |

A run writes in one transaction on every engine: a failed write fails the run and rolls every row back, so a run either
completes or writes nothing. On MySQL this holds for a transactional table (InnoDB); a MyISAM or MEMORY table cannot roll
back, so a failed run keeps the rows written before the failure.

Truncate & insert empties a MySQL table with `TRUNCATE TABLE`. The server refuses it for a table that another table
references by foreign key, whatever the write login can see of that table, so the run fails and nothing is emptied or
cascaded; a table's foreign key to itself does not stop it. On MySQL `TRUNCATE` commits implicitly and cannot be rolled
back, so Beacon starts the transaction again right after it: a Truncate & insert run that fails after the table was
emptied leaves the table empty, never partly written. Run it again once the cause is fixed. Because the table is not
locked between the clear and the commit, two Truncate & insert runs into the same MySQL table at the same time can
both write their rows; schedule jobs that write the same table so they do not overlap.

The rows are written as the source returns them:

- The columns written are those of all source rows. A NULL, or a column a row does not carry, writes NULL, never the
  column's default; in Upsert mode that NULL replaces the existing value. Rows from one query all carry the same columns.
- SQL Server keeps the source's identity values (the table does not generate new ones) and fires the table's insert
  triggers, and checks `CHECK` and foreign-key constraints: a violating row fails the run, which rolls back.
- In Upsert mode two source rows with the same primary key in one batch of 10,000 fail the run on PostgreSQL and SQL
  Server, which rolls back; on MySQL the later row wins.
- A SQL Server table named without a schema is the `dbo` one.
- A PostgreSQL table named without a schema is the one the write target's connection finds on its `search_path`: the
  first schema of `current_schemas(false)` that has a table of that name. The run checks, reads the key of and writes
  that table; a same-named table in a schema off the path is not used, and a name found on no schema of the path fails
  the run as a missing table.
- MySQL sends at most 1,000 rows, 60,000 values or about 1 MB of values per statement, under MySQL 5.7's default
  4 MB `max_allowed_packet`. Its Upsert matches on any unique key of the table, not only the primary key (how
  `ON DUPLICATE KEY UPDATE` works), and keeps `VALUES(col)`, which MariaDB and MySQL 5.7 understand; MySQL 8.0.20 and
  later accept it with a deprecation warning.

### Schedules

The cron schedule is stored with the job and shown on it. Beacon does not start migration runs from it by itself: a run
starts from **Run now**, `POST /beacon/api/migrations/jobs/{id}/runs` or `POST /beacon/api/migrations/jobs/{id}/run`. A host that wants scheduled runs starts them
from its own background job (see [Background jobs](/integrate/background-jobs/)); the approval and write-target checks,
the timeout and the one-run-at-a-time rule apply to those runs too. `IMigrationService.ExecuteMigrationJob` answers a
run that overlaps one starting or in progress with a failed response whose `RunInProgress` is `true` (execution id 0,
nothing run or recorded in the history); it does not throw.

## Run a job

1. Open the job from the jobs list.
2. Click **Run now**.

**Run now** is disabled while the job is disabled or awaiting approval. The run is a background task: the button shows
it queued and running, and when it ends the page reads the job's runs again and says how it went (the run history has its
counts and error).

- **As a background task** (what the page's button does): `POST /beacon/api/migrations/jobs/{id}/runs` answers `202` at
  once with `{ taskId, jobId, state }` and a `Location` header, after the same checks as a run in the request (the
  deployment lock, project access, write targets, an existing enabled job). Read its progress with
  `GET /beacon/api/migrations/jobs/{id}/runs/{taskId}` (MigrationOperator, with the run's access checks; `state`: `0`
  queued, `1` running, `2` succeeded, `3` failed, `4` skipped; `warningCount` is the rows that failed in a run that
  finished; `failureCode` is `run_failed` for a run that failed, whose history row says why). The job runs as the user
  who started it and checks again, right before the run, that they still hold MigrationOperator and meet the job's
  access rules: if not, it is skipped (`forbidden`, on record as a `refused` `migrations.job_run_ended`) and nothing
  runs. A MigrationOperator role stored in Beacon is checked live. A permission only an identity-provider app role grants
  (`Beacon:Authorization:IdpRoles`) lives on the sign-in, which the job does not have: the start records that it came
  from there (`migrations.job_run_requested` carries `permissionSource`), and the job relies on it only within 15
  minutes of the start (or the claim lifetime, if shorter), while the user is still enabled and its security stamp
  unchanged (a role change, a disable or "sign out everywhere" changes it); a run task that waits longer is skipped
  (`forbidden`). A job has one run task queued or running at a time
  (a second start answers `409`); a run task that finds a run of the job already in progress (started another way) is
  skipped (`busy`). The run itself is the same as in the request: the one-run-at-a-time claim, the destination lock, the
  job's timeout, the commit that is never cancelled and the history row that is always closed all apply. A run task left
  running by a host that stopped is given up (`abandoned`) when the next one starts or its progress is read, once the
  job's run history shows its run ended (closed, or left running past the job's timeout plus 10 minutes) or it did not
  reach its run, or no run of it began, within five minutes; runs started another way before the task reached its own
  are not counted. At the latest it is given up after the longest timeout (1,440 minutes) plus 10 minutes. A task given
  up before its run does not start one, and a task that still ends records its own outcome over `abandoned`. The job runs through your host's [`IBeaconScheduler.EnqueueBackgroundTask`](/integrate/background-jobs/);
  a host that does not implement it runs the migration in the request, and the `202` then carries the state it finished in.
- **In the request** (kept for API callers of Beacon 5.0): `POST /beacon/api/migrations/jobs/{id}/run` answers with the
  run's counts once it has ended, which holds the request open for as long as the run takes.

- **One run at a time.** A job runs once at a time, across every host: a run of a job that already has a run starting
  or in progress is refused with `409 Conflict` before it reads or writes anything, and adds no run to the history (the
  refusal is a security event with reason `run_in_progress`).
- **Destination lock.** Inside its write transaction, before it checks or writes the table, a run takes a lock on its
  job in the destination database, without waiting: a PostgreSQL transaction-level advisory lock
  (`pg_try_advisory_xact_lock`), a SQL Server application lock owned by the transaction (`sp_getapplock`) or a MySQL
  named lock (`GET_LOCK`, released after the commit or rollback). If another run of the job holds it (a run whose host
  paused past the in-progress window while a new run started), the run fails with *"Nothing was written: another run of
  this job holds its destination's migration lock"* (security event reason `destination_busy`) and writes nothing. None
  of these locks needs a grant beyond the login's.
- **A commit that cannot be confirmed.** If the destination's commit fails (the connection is lost during it, say),
  Beacon cannot tell whether the rows were written. The run is recorded as failed with *"The destination's commit could
  not be confirmed: check the destination table before running this job again"* (reason `commit_unknown`); nothing is
  rolled back or retried. Look at the table before you run the job again, especially in Insert mode.
- **A host that stops during a run.** Its run stays **Running** and blocks the job until the job's timeout and 10 more
  minutes have passed since it started. The next run after that closes it as **Failed** (*"Abandoned: the host stopped
  during the run"*, security event reason `abandoned`); nothing needs to be done by hand.
- **Timeout.** A run is stopped after the job's timeout (1 to 1,440 minutes; a job stored with another value is held to
  that range) and recorded as failed with *"The run timed out after N minutes"*. The write is one transaction, so a run
  stopped before it commits writes nothing (except a MySQL Truncate & insert, see [Modes](#modes)).

## Jobs and run history

The jobs list (`/beacon/migration-jobs`) is paged on the server and shows **Id**, **Name**, **Source → destination**,
**Mode**, **Enabled**, **Schedule** and **Created**, with a delete action per row. **History** opens the run history of
all jobs.

A job's page shows its definition (source, destination, mode, schedule, enabled, created), **Status**, **Last run** and
the successes and failures among its recent runs, and its runs with **Started**, **Status**, **Duration**, **Rows**,
**Retry** and **Error**.

The run history page (`/beacon/migration-history`) lists runs of every job you can read with **Started**, **Job**,
**Status**, **Rows**, **Failed** and **Rows/s**.

| Status | Meaning |
|--------|---------|
| Running | The run has started. |
| Completed | Every row was written. |
| Partial | Recorded by earlier versions, whose MySQL writes fell back to row by row; runs no longer end in it. The job page labels it **Partial success**. |
| Failed | The read or the write failed, the run timed out or was abandoned by a host that stopped, or the run was refused (not approved, not a write target). The error says why. |

### Delete a job

Click the delete action on the row, or **Delete** on the job's page, and confirm. The job is archived; its run history is
kept.

## Approval of new jobs

The [approval workflow](/security/approvals/) is off by default; a host turns it on in code with
`options.EnableApprovalWorkflow(...)`. While it is on, its `CoversMigrationJobs` switch (on by default) makes every new
job wait for a second person's approval before it writes anything:

- **A new job is submitted for approval**, with its creator as the requester. The create answers with
  `awaitingApproval: true` and the message *"Submitted for approval: the job does not run, manually or on its schedule,
  until a second person approves it."* A caller Beacon cannot name (an API key without an active owner, for example)
  creates nothing (`403`) unless the host sets `AllowSelfApproval`.
- **An Approver (or an Admin) other than the creator reviews it** on the **Approvals** page under **Migration jobs**. The
  review shows the definition: source and destination data sources, the source query (SQL or JSON steps), the destination
  table, the mode and the transformation script. The approval covers exactly that definition. Name, description,
  schedule, retry, timeout and enabled are not part of it. Someone without the Approver permission who reads both of the
  job's data sources can open the request to see what waits, but cannot decide it.
- **No run starts before the approval.** A run is refused and recorded as failed with *"Not run: the job's definition
  waits for approval. A second person approves it on the approvals page; until then the job does not run."*, and a
  Warning is logged with the job id. The job shows **Awaiting approval** with a link to the request, and **Run now** is
  disabled.
- **A job runs only the definition that was approved.** If the stored definition differs from the approved one (a change
  made outside Beacon), the job does not run until a matching definition is approved.
- **A rejected job never runs.** Its page shows that the definition was rejected, with the reviewer's comment and the
  time of the decision (`lastApprovalDecision` on the job). Delete it and create the job again with a corrected
  definition, which is submitted again.
- There is no withdraw action: an Approver rejects the request, or the creator deletes the job.
- Nobody approves their own job unless the host sets `AllowSelfApproval`, and a request is decided once.

:::caution[Upgrading]
Jobs that exist when the workflow is turned on are not reviewed: they run as before. A job created while the workflow is
off is not reviewed later. Turning `CoversMigrationJobs` (or the workflow) off again lets waiting and rejected jobs run as
they are; startup logs their count.
:::

## Destination privileges

Only a [migration write target](#migration-write-targets)'s login needs write grants. Grant it only what the job's mode
uses, on the destination table only:

| Mode | PostgreSQL | MySQL | SQL Server |
|------|------------|-------|------------|
| Insert only | `INSERT` (written with `COPY … FROM STDIN`) | `INSERT` | `INSERT` (bulk copy); `ALTER` on the table too when the job writes an identity column |
| Insert or update | `INSERT`, `UPDATE`, `SELECT` on the written columns (`ON CONFLICT` reads them, also on a table whose columns are all key columns), and `TEMPORARY` on the database (rows are staged in a temporary table; PostgreSQL grants it to `PUBLIC` by default) | `INSERT`, `UPDATE`; `SELECT` on the first key column too when every column is a key column (the update assigns it to itself) | `SELECT`, `INSERT`, `UPDATE` (staged in a session temporary table); `ALTER` on the table too when the job writes an identity column (`IDENTITY_INSERT`) |
| Truncate & insert | the `TRUNCATE` privilege, `INSERT` | `DROP` on the table (MySQL requires it to truncate), `INSERT` | `ALTER` on the table (SQL Server requires it to truncate), `INSERT` |

On PostgreSQL every mode also needs `USAGE` on the table's schema. Every mode also reads `information_schema` to check
that the table exists (and, for Upsert, its primary key). Some of
these grants are DDL-grade (`ALTER`, `DROP`, the `TRUNCATE` privilege), which is why such a login belongs on a write
target that Beacon never reads, not on an ordinary data source.

## REST API

All routes are under `/beacon/api/migrations`, mapped only while `Beacon:Migrations:Enabled` is `true`, and need the
MigrationOperator permission.

| Method | Route | What it does |
|--------|-------|--------------|
| `GET` | `/jobs` | List jobs (paged: `page`, `pageSize`, `sort`). |
| `GET` | `/jobs/{id}` | One job. |
| `POST` | `/jobs` | Create a job. |
| `POST` | `/jobs/{id}/runs` | Start a run of a job as a background task (`202` with its ticket). |
| `GET` | `/jobs/{id}/runs/{taskId}` | The state of a run task. |
| `POST` | `/jobs/{id}/run` | Run a job now, in the request (kept for 5.0 callers). |
| `DELETE` | `/jobs/{id}` | Archive a job. |
| `GET` | `/executions` | List runs (paged). |

## Troubleshooting

| Symptom | Check |
|---------|-------|
| No **Data Migration** entry | `Beacon:Migrations:Enabled` is `true`, and you hold MigrationOperator (or are an Admin). |
| The destination picker is empty | An Admin has marked the destination as a migration write target. |
| **Run now** is disabled | The job is enabled and not awaiting approval. |
| Run failed: another run holds the destination's migration lock | A run of the same job is still writing (its host was slow or paused). Wait for it to end, then run the job again. |
| Run failed: the destination's commit could not be confirmed | Check the destination table for the run's rows before you run the job again. |
| Run answers `409 Conflict` | Another run of the job is starting or in progress (or a host stopped during one less than the job's timeout plus 10 minutes ago). Wait for it to end, then run it again. |
| Run failed on a job with a transformation script | Create the job again with **Transformation script** empty. |
| Run failed: row budget exceeded | The source returned more than `Beacon:Migrations:MaxRows` rows. Split the job or raise the budget. |
| Run failed: table does not exist | The destination table exists in the write target's database, and the name includes its schema if needed. |
| Run failed: missing primary key columns | In Upsert mode, the source query returns every primary key column of the destination table. |
| Run failed: permission denied on the destination | The write target's login has the [destination privileges](#destination-privileges) of the job's mode. |

## Related

- [Data Sources](/features/data-sources/): connections and the write-target flag
- [Queries](/features/queries/): how a source query is read
- [Approvals](/security/approvals/): the change approval workflow
- [Configuration](/getting-started/configuration/#data-migration): `Beacon:Migrations`
