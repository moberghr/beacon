---
name: beacon-integrate
description: Embed Beacon's data product (monitored databases, multi-step queries, alerts, data movement, and the MCP server over them) in an existing ASP.NET Core application with the Moberg.Beacon.* packages. Use when someone asks to add Beacon to their app, host Beacon's UI at /beacon, monitor their databases with Beacon, or give AI clients governed SQL access through Beacon — not for an MCP server over the application's own endpoints only, which is the beacon-gateway skill.
---

# Embed Beacon in an application

The integration guide lives in Beacon's docs, and Beacon refuses to start on most wiring mistakes with a message that
says the fix. Follow the guide, not memory:

1. **Read the guide first:** fetch `https://moberghr.github.io/beacon/llms-integrate.txt`. Its first page is a numbered
   checklist; follow it in order. For a quicker first run, the recipe
   `https://moberghr.github.io/beacon/cookbooks/embed-postgresql-warp.md` (or `embed-sqlserver-hangfire.md`) is the
   same path with fewer options.
2. **Read the application before deciding anything.** Find and note:
   - the hosting model (`WebApplication` minimal APIs or MVC), and whether something already lives under `/beacon`;
   - sign-in: `AddAuthentication(...)` and its default scheme, cookie and OIDC set-up, any `IClaimsTransformation`;
   - the databases: one for Beacon's own data (PostgreSQL with the `pgvector` extension available, or SQL Server), and
     the engines Beacon should monitor;
   - the job runner (Moberg Warp, Hangfire, Quartz.NET, or none): Beacon runs no background work itself;
   - a migrator project or deploy step, and whether several web replicas start at once;
   - a reverse proxy in front of the app (forwarded headers, the public URL);
   - `Directory.Packages.props` (central package versions: add versions there, never in a `.csproj`).
3. **Ask the decisions the guide lists** (metadata database, who signs people in, job runner, engines, AI on or off,
   MCP exposed or not, four-eyes approvals, data migration). Propose the defaults, never assume them.
4. **Wire it:**
   - packages: a metadata provider (`Moberg.Beacon.Core.PostgreSql` / `Moberg.Beacon.Core.SqlServer`),
     `Moberg.Beacon.Api`, `Moberg.Beacon.UI`, one `Moberg.Beacon.Connector.*` per engine, and `Moberg.Beacon.AI` +
     `Moberg.Beacon.MCP` for MCP;
   - secrets: `ConnectionStrings:BeaconContext` and `Beacon:EncryptionKey` (`openssl rand -base64 32`) in user secrets
     or the secret store, never in a committed file;
   - an `IBeaconScheduler` on the job runner, with every job marking its scope as system work;
   - `AddBeaconServices(...)` with connectors chained, `UsePostgreSql()` / `UseSqlServer()` last, then the cookie
     authentication (base path `/beacon`), Data Protection, `AddBeaconApiAuthorization()`, `AddBeaconApiServices()`,
     antiforgery, AI and MCP registrations from the guide's `Program.cs`;
   - the middleware in the guide's order (`UseBeaconAuthentication()` replaces `UseAuthentication()`), then the
     endpoints (`MapBeaconApi`, login and setup endpoints, `MapBeaconMcp`, `MapMcpDiscovery`, `MapBeaconUi`);
   - the schema: `Beacon.Core.ServiceConfiguration.UseBeacon(app.Services, createSchema: true)` at startup, or
     `BeaconPostgreSqlMigrator` /
     `BeaconSqlServerMigrator` from the migration step.
5. **Run it and check, in order:** the console prints the first-run setup token; `/beacon/setup` creates the first
   administrator; `GET /beacon/api/health` answers 200; `/beacon/home` loads after sign-in;
   `/.well-known/mcp/server-card.json` lists the MCP tools. Build and run the whole test suite.
6. **Data sources** get a read-only database login each, with TLS that verifies the server. Never disable certificate
   validation to make a connection work.
7. **Before production:** walk `https://moberghr.github.io/beacon/integrate/production.md` with the person.

Stop and ask before anything outward-facing: creating cloud resources, changing an identity provider, pushing, deploying.
