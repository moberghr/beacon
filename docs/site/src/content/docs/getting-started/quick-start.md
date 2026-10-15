---
title: Quick start
description: "Run the Beacon sample host from a clone and send your first alert end to end: data source, project, query, recipient and subscription."
---

This guide runs the sample host on your machine and walks you to a first alert: a scheduled query whose rows arrive at a webhook. It takes about 20 minutes.

You will:

1. Start PostgreSQL and create Beacon's schema.
2. Run the sample host and the UI.
3. Create the first admin.
4. Add a data source, a project, a query, a recipient and a subscription.
5. Trigger a run and see the notification.

## Prerequisites

| Tool | Version | Check |
|---|---|---|
| .NET SDK | 10 | `dotnet --version` |
| Node.js and npm | Node 22, npm 11 | `node -v`, `npm -v` |
| Docker | any recent | `docker --version` |
| `dotnet-ef` | 10 | `dotnet ef --version` |
| `openssl` | any | `openssl version` |

- The UI's lockfile needs npm 11. If `npm -v` prints 10, run `npm install -g npm@11`.
- Install the EF Core tools once with `dotnet tool install --global dotnet-ef`.
- Instead of Docker you can use any PostgreSQL server that has the [pgvector](https://github.com/pgvector/pgvector) extension available.

Clone the repository and run every command below from its root:

```bash
git clone https://github.com/moberghr/beacon.git
cd beacon
```

## Step 1: Start PostgreSQL

Start PostgreSQL with pgvector and a database named `beacon`:

```bash
docker run -d --name beacon-pg \
  -e POSTGRES_PASSWORD=beacon \
  -e POSTGRES_DB=beacon \
  -p 5432:5432 \
  pgvector/pgvector:pg17
```

This database holds Beacon's own data and the sample host's background jobs. In this guide it is also the database you monitor.

## Step 2: Set the two required secrets

The sample host reads its connection string and encryption key from [user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets). User secrets are loaded only in the Development environment, which the launch profile in step 4 sets.

```bash
dotnet user-secrets --project src/Beacon.SampleProject set \
  "ConnectionStrings:BeaconContext" \
  "Host=localhost;Port=5432;Database=beacon;Username=postgres;Password=beacon"

dotnet user-secrets --project src/Beacon.SampleProject set \
  "Beacon:EncryptionKey" "$(openssl rand -base64 32)"
```

This connection string, without TLS, is for the local container only; a deployed host's database connection belongs in its environment or secret store.

The encryption key protects connection strings and other secrets at rest. Keep it: values encrypted with it cannot be read with another key.

## Step 3: Create Beacon's schema

The sample host migrates only its job-runner tables at startup. Beacon's own schema is created with the EF Core tools:

```bash
dotnet ef database update \
  --project src/Beacon.Core.PostgreSql \
  --startup-project src/Beacon.SampleProject \
  --context PostgreSqlBeaconContext
```

The command builds the sample host and its dependencies, then applies every migration. The migrations create the `beacon` schema and the `vector` extension, so the login in the connection string needs the rights to create both (the `postgres` user in the container has them). The output ends with `Done.`

## Step 4: Run the sample host

Trust the ASP.NET Core development certificate once, then start the host with the `https` launch profile:

```bash
dotnet dev-certs https --trust
dotnet run --project src/Beacon.SampleProject --launch-profile https
```

The profile sets `ASPNETCORE_ENVIRONMENT=Development` and listens on `https://localhost:7187` and `http://localhost:5296`.

While no admin exists, the host prints a one-time setup token to the console (standard error, not the log):

```text
Beacon first-run setup token (enter it on the setup page): 3f1c…
```

Copy it. A new token is generated every time the host starts until setup is done.

## Step 5: Open the UI

Pick one way to serve the React UI. Either way, the UI talks to the host from step 4.

### Option A: Vite dev server (no build)

In a second terminal:

```bash
npm ci --prefix src/Beacon.UI/web
npm run dev --prefix src/Beacon.UI/web
```

Open [http://localhost:5173/beacon/setup](http://localhost:5173/beacon/setup). Vite proxies `/beacon/api` and `/beacon/mcp` to `https://localhost:7187`.

### Option B: Build the UI once

```bash
npm ci --prefix src/Beacon.UI/web
npm run build --prefix src/Beacon.UI/web
```

The build writes to `src/Beacon.UI/wwwroot/beacon`. Restart the host from step 4 so it picks up the files, then open [https://localhost:7187/beacon/setup](https://localhost:7187/beacon/setup).

:::tip[Exploring the UI without a backend]
`npm run dev:mock --prefix src/Beacon.UI/web` serves the UI at `http://localhost:5173/beacon/` with mock data and no backend. Nothing you do there is saved.
:::

## Step 6: Create the first admin

On the setup page, fill in:

- **Setup token**: the token from step 4.
- **Username**, and optionally **Email** and **Display name**.
- **Password** and **Confirm password**: at least 8 characters, with an uppercase letter, a lowercase letter, a digit and a special character.

Click **Create super admin**, then **Go to sign in**, and sign in with the account you just created. This first account is the super admin and holds the Admin role, so it can do everything in this guide.

To confirm the host is healthy:

```bash
curl -k https://localhost:7187/beacon/api/health
# {"status":"ok"}
```

## Step 7: Create a read-only login to monitor

Beacon runs monitoring SQL read-only, but every data source should still use a login that cannot write. Create one in the same container:

```bash
docker exec -i beacon-pg psql -U postgres -d beacon <<'SQL'
CREATE ROLE beacon_reader LOGIN PASSWORD 'reader';
ALTER ROLE beacon_reader SET default_transaction_read_only = on;
SQL
```

## Step 8: Add a data source

1. Open **Data Sources** (`/beacon/data-sources`) and click **New data source**.
2. **Type**: choose **Database**, then **Next**.
3. **Connection**:
   - **Name**: `Local PostgreSQL`
   - **Database engine**: **PostgreSQL**
   - **Connection string**: `Host=localhost;Port=5432;Database=beacon;Username=beacon_reader;Password=reader`

   Click **Next**.
4. **Test & save**: click **Test connection** and wait for **Connected**, then click **Create data source**.

Beacon encrypts the connection string and loads the database's metadata. Until the data source is linked into a project, only Admins can use it.

:::note[Local development only]
The local container has no TLS, so this connection does not authenticate the server. The sample host accepts it because its Development configuration sets `Beacon:DataSources:Transport:AllowInsecure`. Outside Development Beacon refuses it: a real data source needs a connection that verifies the server's certificate, for example `Host=db.example.com;Port=5432;Database=sales;Username=beacon_reader;Password=...;SSL Mode=VerifyFull`. See [Transport security (TLS)](/features/data-sources/#transport-security-tls).
:::

## Step 9: Create a project

1. Open **Projects** (`/beacon/projects`) and click **New project**.
2. **Project name**: `Getting started`. Add a **Description** if you like.
3. Under **Data sources**, pick `Local PostgreSQL`.
4. Click **Create project**.

The project is restricted: only its members and Admins see it. You are its project admin.

## Step 10: Write a query

An alert query returns rows when something needs attention, and no rows when all is well. A run with no rows never notifies. For a first test, use a query that always returns rows: the databases that have open connections. Beacon's own connections guarantee at least one.

1. Open **Queries** (`/beacon/queries`) and click **New query**.
2. **Query name**: `Databases with connections`. **Description**: `Connections per database`.
3. In the step:
   - **Step name**: `Connections`
   - **Target database**: `Local PostgreSQL`
   - **SQL query**:

     ```sql
     SELECT datname, numbackends
     FROM pg_stat_database
     WHERE numbackends > 0
     ```

4. Click **Save & run**. Beacon saves the query, opens it and runs it. You should see at least one row.

For a real alert, raise the threshold in the `WHERE` clause so rows appear only when there is a problem, for example `WHERE numbackends > 50`.

## Step 11: Add a recipient

A recipient must exist before you create the subscription. A webhook is the quickest to try:

1. Get a temporary public HTTPS endpoint from a request-inspection service such as [webhook.site](https://webhook.site).
2. Open **Recipients** (`/beacon/recipients`) and click **Add recipient**.
3. **Notification type**: **Webhook**. **Name**: `Test webhook`. **Webhook URL**: your endpoint's URL.
4. Click **Create recipient**.

Other channels:

| Channel | What you need |
|---|---|
| Email | The sample host sends email over SMTP. Set `Beacon:Smtp:Host`, `Beacon:Smtp:FromAddress`, `Beacon:Smtp:Username` and `Beacon:Smtp:Password` (and `Port`, default `465`; `EnableSsl`, default `true`) in user secrets and restart the host. |
| Microsoft Teams | A Teams Workflows webhook URL. Beacon accepts Teams URLs on `*.webhook.office.com`, `*.logic.azure.com` and `*.environment.api.powerplatform.com`. |
| Slack | A Slack incoming-webhook URL on `hooks.slack.com`. |
| Jira | A connection string in the form `domain;project-key;email;api-key`. |

See [Notifications](/features/notifications/) for the destination rules.

## Step 12: Create the subscription

1. Open **Subscriptions** (`/beacon/subscriptions`) and click **New subscription**. (The query's page also has **Add subscription**.)
2. **Query** step:
   - **Query**: `Databases with connections`
   - **Cron expression**: `*/5 * * * *` (every five minutes; standard 5-field cron)
3. **Notify** step:
   - **Send notification**: **Every execution**
   - **Recipients**: tick `Test webhook`
4. **Results** and **Anomaly**: keep the defaults.
5. **Review**: click **Create subscription**.

The subscription runs as you, its owner. The sample host's job runner (Warp) picks it up on the schedule.

## Step 13: Trigger a run and check the result

You do not need to wait five minutes:

1. On the subscription's page, click **Test now**.
2. Your webhook endpoint receives a `POST` with a JSON body: the subscription and data source names, the SQL, the row count and the rows.
3. Open **Notifications** (`/beacon/notifications`) to see the delivery, its status and the stored result.

You have a working alert. Before you leave it running, click **Archive** on the subscription, or create a new one on a query with a real threshold.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| The host stops with `Beacon:DataProtection:ApplicationName must be set outside Development` | The host did not run in Development, so user secrets were not loaded either. Start it with `--launch-profile https`. |
| The host stops with an `EncryptionKey` error | The key is missing or not valid base64 of 32 random bytes. Repeat step 2 and check `dotnet user-secrets --project src/Beacon.SampleProject list`. |
| `More than one DbContext was found` from `dotnet ef` | Add `--context PostgreSqlBeaconContext`, as in step 3. |
| `relation "…" does not exist` in the host's log | Beacon's schema was not migrated. Run step 3. |
| `extension "vector" is not available` | The PostgreSQL server lacks pgvector. Use the `pgvector/pgvector` image or install the extension on your server. |
| `404` at `/beacon/setup` | The UI is not built. Use Vite (option A) or build it (option B) and restart the host. |
| Nothing at `https://localhost:7187/` | The UI lives at `/beacon`, not at the site root. |
| The browser warns about the certificate | Run `dotnet dev-certs https --trust` and restart the browser. |
| The subscription runs but nothing arrives | The query returned no rows (no notification by design), the trigger skipped the run, or the destination is not allowed. Check the run's status in **Notifications**. |

## Next steps

- [Core concepts](/getting-started/concepts/): how projects, queries and subscriptions fit together.
- [Run the sample host](/getting-started/installation/): everything else the sample host can do.
- [Queries](/features/queries/): multi-step queries and parameters.
- [MCP server](/features/mcp-server/): connect an AI assistant to the same data.
- [Integrate Beacon](/integrate/): run Beacon inside your own application.
