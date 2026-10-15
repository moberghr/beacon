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
[Security model](/features/security-model/)). Listing jobs, opening one, creating, running and deleting jobs and reading
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
| **Migration mode** | Yes | Insert only | **Insert only**, **Insert or update** or **Truncate & insert**. See [Modes](#modes). |
| **Cron schedule** | No | empty | Standard 5-field cron, stored with the job. See [Schedules](#schedules). |
| **Max retries** | No | 3 | 0 to 10. Stored with the job and shown on each run as **Retry**. A failed run is not retried automatically. |
| **Timeout (minutes)** | No | 30 | 1 to 1,440. Stored with the job. |
| **Enable migration job** | No | on | A disabled job does not run. |
| **Validate query before execution** | No | on | Stored with the job. |
| **Transformation script (optional)** | No | empty | Leave it empty. A job with a script fails when it runs. |

The job is saved when the form is valid; its SQL is not run at creation. The destination table is checked for existence
on every run.

:::note
A job cannot be edited after it is created; no route changes one. To change its query, destination, mode, schedule or
whether it is enabled, create a new job and delete the old one. The old job's run history is kept.
:::

### Source query

The source is read the way every query runs (see [Queries](/features/queries/)): through the read-only execution gate
and the source's read-only path, within `Beacon:Query:StatementTimeoutSeconds` (default 120 seconds) per statement.

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
| **Insert or update** (Upsert) | Stages the rows in a temporary table, then inserts new rows and updates existing ones by primary key. | A primary key on the destination table, and every key column in each source row (otherwise the run fails). Without a primary key the job inserts, as in Insert only. |
| **Truncate & insert** | Empties the destination table, then inserts every source row. | |

:::caution
**Truncate & insert** deletes every row in the destination table on each run. Run the job by hand against a test table
before you enable it on real data.
:::

How the write is done per engine:

| Engine | Insert | Upsert |
|--------|--------|--------|
| PostgreSQL | `COPY … FROM STDIN (FORMAT BINARY)` | `COPY` into a temporary table, then `INSERT … ON CONFLICT` |
| SQL Server | bulk copy | bulk merge, staged in a session temporary table |
| MySQL | bulk insert; if it fails, row by row | bulk insert into a temporary table, then `INSERT … ON DUPLICATE KEY UPDATE` |

On PostgreSQL and SQL Server a failed write fails the run. On MySQL, when the bulk insert fails, Beacon inserts the rows
one at a time: rows that fail are counted and listed in the run's error, and the run ends as **Partial**. It stops after
more than 100 failed rows.

### Schedules

The cron schedule is stored with the job and shown on it. Beacon does not start migration runs from it by itself: a run
starts from **Run now** or from `POST /beacon/api/migrations/jobs/{id}/run`. A host that wants scheduled runs starts them
from its own background job (see [Background jobs](/integrate/background-jobs/)); the approval and write-target checks
apply to those runs too.

## Run a job

1. Open the job from the jobs list.
2. Click **Run now**.

**Run now** is disabled while the job is disabled or awaiting approval. The run happens in the request; the page then
shows it in the job's runs.

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
| Partial | Some rows failed (MySQL row-by-row fallback); see the run's error. The job page labels it **Partial success**. |
| Failed | The read or the write failed, or the run was refused (not approved, not a write target). The error says why. |

### Delete a job

Click the delete action on the row, or **Delete** on the job's page, and confirm. The job is archived; its run history is
kept.

## Approval of new jobs

The [approval workflow](/features/approvals/) is off by default; a host turns it on in code with
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
| Insert only | `INSERT` (written with `COPY … FROM STDIN`) | `INSERT` | `INSERT` (bulk copy) |
| Insert or update | `INSERT`, `UPDATE`, and `TEMPORARY` on the database (rows are staged in a temporary table) | `INSERT`, `UPDATE`, `CREATE TEMPORARY TABLES` | `INSERT`, `UPDATE` (staged in a session temporary table) |
| Truncate & insert | the `TRUNCATE` privilege, `INSERT` | `DROP` on the table (MySQL requires it to truncate), `INSERT` | `ALTER` on the table (SQL Server requires it to truncate), `INSERT` |

Every mode also reads `information_schema` to check that the table exists (and, for Upsert, its primary key). Some of
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
| `POST` | `/jobs/{id}/run` | Run a job now. |
| `DELETE` | `/jobs/{id}` | Archive a job. |
| `GET` | `/executions` | List runs (paged). |

## Troubleshooting

| Symptom | Check |
|---------|-------|
| No **Data Migration** entry | `Beacon:Migrations:Enabled` is `true`, and you hold MigrationOperator (or are an Admin). |
| The destination picker is empty | An Admin has marked the destination as a migration write target. |
| **Run now** is disabled | The job is enabled and not awaiting approval. |
| Run failed on a job with a transformation script | Create the job again with **Transformation script** empty. |
| Run failed: row budget exceeded | The source returned more than `Beacon:Migrations:MaxRows` rows. Split the job or raise the budget. |
| Run failed: table does not exist | The destination table exists in the write target's database, and the name includes its schema if needed. |
| Run failed: missing primary key columns | In Upsert mode, the source query returns every primary key column of the destination table. |
| Run failed: permission denied on the destination | The write target's login has the [destination privileges](#destination-privileges) of the job's mode. |

## Related

- [Data Sources](/features/data-sources/): connections and the write-target flag
- [Queries](/features/queries/): how a source query is read
- [Approvals](/features/approvals/): the change approval workflow
- [Configuration](/getting-started/configuration/#data-migration): `Beacon:Migrations`
