---
title: Run the sample host
description: Reference for Beacon.SampleProject, the sample host in the repository — what it wires, the configuration it reads, ports, Warp jobs, the UI build, running outside Development and publishing a release.
---

`src/Beacon.SampleProject` is the composition root in the Beacon repository: an ASP.NET Core application that wires every Beacon package together. It is how you evaluate Beacon and how contributors run it. The [Quick start](/getting-started/quick-start/) takes you from a clone to a first alert; this page is the reference for everything else the sample does.

:::caution[A reference, not a product]
The sample host is never shipped. It hard-codes choices a real deployment makes for itself: Warp as the job runner, SMTP for email, Beacon's own user store, no Data Protection key encryption. Run Beacon in production by embedding the packages in your own host. See [Integrate Beacon](/integrate/).
:::

## What the sample includes

| Area | What the sample wires | Where |
|---|---|---|
| Beacon services | `AddBeaconServices` with AI on (`UseAI = true`), authorization on, the login form, Beacon's user store (`DatabaseAuthenticationProvider`) and user management with first-run setup. The approval workflow is not enabled. | `Program.cs` |
| Metadata database | PostgreSQL (`.UsePostgreSql()`), connection string `ConnectionStrings:BeaconContext`. | `Program.cs` |
| Connectors | All nine: PostgreSQL, SQL Server, MySQL, CloudWatch, Azure Synapse, Snowflake, Databricks, BigQuery and REST API. | `Program.cs` |
| Background jobs | [Moberg Warp](https://moberghr.github.io/warp/) on PostgreSQL, with `BeaconScheduler` as the `IBeaconScheduler` implementation and one Warp job per Beacon operation. Dashboard at `/warp`. | `Warp/`, `Services/BeaconScheduler.cs` |
| Sign-in | Cookie sign-in under `/beacon`, Data Protection keys in Beacon's database, optional OIDC/SSO, and bearer JWT for MCP clients when OIDC has a `McpJwksEndpoint`. | `Program.cs` |
| API, UI and MCP | REST API and SignalR hub under `/beacon/api`, the React UI at `/beacon`, the MCP server at `/beacon/mcp`, the MCP discovery documents, and the OpenAPI document at `/openapi/v1.json`. | `Program.cs` |
| Email | `BeaconMailSender`, an SMTP adapter configured with `Beacon:Smtp:*`. | `Services/BeaconMailSender.cs` |
| Error responses | Exceptions under `/beacon/api` become RFC 7807 `application/problem+json` responses. | `Middleware/ApiExceptionMiddleware.cs` |
| Telemetry | OpenTelemetry traces and metrics, only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. | `Program.cs` |
| Reverse proxy | Forwarded headers from trusted proxies, only when `Beacon:ForwardedHeaders:Enabled` is `true`. | `Program.cs` |
| Timeouts | Kestrel keep-alive and request-header timeouts and the default `HttpClient` timeout set to 5 minutes, for long AI calls. | `Program.cs` |

The Warp jobs, `BeaconMailSender`, the exception middleware, the telemetry and forwarded-headers blocks and the timeouts are sample code, not part of any package. A host that embeds Beacon provides its own.

## Configuration

The sample reads standard ASP.NET Core configuration: `appsettings.json`, `appsettings.{Environment}.json`, user secrets (Development only), environment variables and command-line arguments. Environment variables use `__` for `:`, for example `Beacon__EncryptionKey`.

`appsettings.json` holds the non-secret defaults and commented examples. `appsettings.Development.json` sets only logging. Secrets never go in either file.

| Key | Required | What it does |
|---|---|---|
| `ConnectionStrings:BeaconContext` | Yes | Beacon's metadata database (PostgreSQL in the sample). |
| `Beacon:EncryptionKey` | Yes | Base64 of at least 32 random bytes. Encrypts connection strings and other secrets at rest. Checked at startup in every environment. |
| `ConnectionStrings:WarpContext` | No | PostgreSQL database for Warp. Defaults to `BeaconContext`. |
| `Beacon:BaseUrl` | No | Base for links in notifications, built as `{BaseUrl}/notifications/{id}`. Include `/beacon`, for example `https://beacon.example.com/beacon`. Can be changed later in **Admin Settings**. |
| `Beacon:PublicBaseUrl` | Outside local use | Public origin advertised in the MCP discovery documents. Unset, Beacon uses the request's Host header and logs a warning. |
| `Beacon:DataProtection:ApplicationName` | Outside Development | Data Protection application name, one per deployment. |
| `Beacon:DataProtection:AllowUnencryptedKeys` | No | Acknowledges a key ring stored without encryption at rest. See [Running outside Development](#running-outside-development). |
| `Beacon:UserManagement:SetupToken` | Recommended outside Development | First-run setup token, at least 32 characters. Unset, each process prints its own random token to the console. |
| `Beacon:UserManagement:LocalLogin:Enabled`, `BreakGlassUserNames` | No | Password sign-in on or off, and up to three break-glass accounts that keep a password when it is off. |
| `Beacon:Authentication:Oidc:*` | No | OIDC/SSO. See [Sign-in with OIDC](#sign-in-with-oidc-and-jwt-for-mcp). |
| `Beacon:Mcp:Callers:*` | No | Entra callers admitted to `/beacon/mcp`. See [Entra ID callers](/features/mcp-entra-callers/). |
| `Beacon:ForwardedHeaders:Enabled`, `KnownProxies` | No | Trust `X-Forwarded-For` and `X-Forwarded-Proto` from the listed proxy IP addresses. An empty list trusts none. |
| `Beacon:Smtp:Host`, `FromAddress`, `Username`, `Password`, `Port`, `EnableSsl` | For email | SMTP for email recipients. `Port` defaults to `465`, `EnableSsl` to `true`. A missing required key fails the send, not the startup. |
| `Beacon:LLM:*` | For AI features | The LLM provider. See [AI providers](/integrate/ai/). |
| `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_SERVICE_NAME` | No | Turn on OpenTelemetry export. The service name defaults to `beacon`. |

Every other key (query limits, MCP locks, notification policy, API keys, sessions, authorization) is a Beacon library setting and works the same in any host. See [Configuration](/getting-started/configuration/).

## Ports and launch profiles

`src/Beacon.SampleProject/Properties/launchSettings.json` has three profiles. All of them set `ASPNETCORE_ENVIRONMENT=Development`.

| Profile | URLs | Use |
|---|---|---|
| `http` | `http://localhost:5296` | The default for `dotnet run` without `--launch-profile`. |
| `https` | `https://localhost:7187`, `http://localhost:5296` | The one to use. The Vite dev server proxies to `https://localhost:7187`. |
| `IIS Express` | IIS Express ports | Visual Studio on Windows. |

```bash
dotnet run --project src/Beacon.SampleProject --launch-profile https
```

With `--no-launch-profile` the environment is Production: user secrets and `appsettings.Development.json` are not loaded, and the host refuses to start until the [production settings](#running-outside-development) are in place.

In Development, HTTP requests are redirected to HTTPS except under `/beacon/mcp`, so local MCP clients can connect over `http://localhost:5296/beacon/mcp`. Outside Development every path is redirected, including `/beacon/mcp`.

## Beacon's database

The sample host does not create or migrate Beacon's schema. Run the EF Core migrations yourself, before the first start and after every update:

```bash
dotnet tool install --global dotnet-ef

dotnet ef database update \
  --project src/Beacon.Core.PostgreSql \
  --startup-project src/Beacon.SampleProject \
  --context PostgreSqlBeaconContext
```

The command reads the same configuration as the host, in the Development environment, so user secrets apply. The migrations create the `beacon` schema and the pgvector `vector` extension, so the login needs the rights for both, or a DBA creates them first.

The sample registers PostgreSQL for Beacon's data. To use SQL Server instead, change `.UsePostgreSql()` to `.UseSqlServer()` in `Program.cs`, apply the migrations from `src/Beacon.Core.SqlServer`, and set `ConnectionStrings:WarpContext` to a PostgreSQL database, because Warp needs PostgreSQL. See [Metadata database](/integrate/database/).

## Background jobs (Warp)

At startup the sample migrates Warp's own `warp` schema and registers its recurring jobs. Subscriptions are scheduled through `BeaconScheduler` as you create them.

| Job | Schedule | What it does |
|---|---|---|
| `mcp-learning-aggregate` | `0 */6 * * *` | Mines MCP usage signals into learned patterns. |
| `mcp-learning-cleanup` | `0 3 * * *` | Deletes learning signals past their retention. |
| `mcp-audit-retention` | `30 3 * * *` | Purges MCP audit rows older than `Beacon:Mcp:Audit:RetentionDays`. Does nothing when that is unset. |
| `mcp-embedding-reindex` | `0 */12 * * *` | Re-indexes metadata and examples for semantic search. |
| `mcp-docchunk-reindex` | `0 */12 * * *` | Re-chunks and re-embeds project documentation. |

While `Beacon:PreviousEncryptionKeys` lists a key, every start also enqueues one run of the re-encryption job, which moves stored values to the current key.

- Warp polls every second and runs at most `min(2 × CPU count, 20)` workers.
- Failed jobs are not retried automatically. They stay failed for you to investigate.
- The dashboard at `/warp` is for signed-in users with the Admin role.

See [Background jobs](/integrate/background-jobs/) for implementing `IBeaconScheduler` on another job runner.

## The UI

The React app lives in `src/Beacon.UI/web`. The `Beacon.UI` project serves the built files from `src/Beacon.UI/wwwroot/beacon` at `/beacon`. The repository does not contain a built UI.

| Goal | Command (from the repository root) |
|---|---|
| Install dependencies | `npm ci --prefix src/Beacon.UI/web` |
| Build once for the host | `npm run build --prefix src/Beacon.UI/web`, then restart the host. Output: `src/Beacon.UI/wwwroot/beacon`. |
| Develop with hot reload | `npm run dev --prefix src/Beacon.UI/web`, then open `http://localhost:5173/beacon/`. Proxies `/beacon/api` and `/beacon/mcp` to `https://localhost:7187`, or to `BEACON_BACKEND_URL` when set. |
| Explore without a backend | `npm run dev:mock --prefix src/Beacon.UI/web`, mock data in the browser. |
| Run the UI tests | `npm test --prefix src/Beacon.UI/web` |
| Regenerate the API client | `npm run codegen --prefix src/Beacon.UI/web`, with the host running. Reads `/openapi/v1.json`. |

A Debug build of the .NET solution does not build the React app. A Release build does, and so does a Debug build with `-p:BuildReactInDebug=true`. Both run `npm ci` first when `node_modules` is missing. The UI needs Node.js 22 and npm 11.

## Sign-in with OIDC and JWT for MCP

The sample always offers Beacon's login form and user store. To add single sign-on, fill in `Beacon:Authentication:Oidc` and set `Enabled` to `true`:

```json
{
  "Beacon": {
    "Authentication": {
      "Oidc": {
        "Enabled": true,
        "Authority": "https://login.microsoftonline.com/<tenant-id>/v2.0",
        "ClientId": "<client-id>",
        "AllowedTenants": ["<tenant-id>"],
        "DisplayName": "Microsoft",
        "McpJwksEndpoint": "https://login.microsoftonline.com/<tenant-id>/discovery/v2.0/keys"
      }
    }
  }
}
```

- Put `ClientSecret` in user secrets or an environment variable (`Beacon__Authentication__Oidc__ClientSecret`), never in the file.
- Startup fails with SSO on and no `AllowedTenants`, unless `AllowAnyTenant` is `true` for a provider that issues no tenant claim.
- `McpJwksEndpoint` is a sample convention: when it is set, the sample also accepts bearer JWTs on the API and on `/beacon/mcp`, validated against those signing keys with the `Authority` as issuer and the `ClientId` as audience. Which tokens become MCP callers is decided by `Beacon:Mcp:Callers`.

Every OIDC option is in [Configuration](/getting-started/configuration/). How sign-in works is in [Sign-in & identity](/integrate/authentication/).

## Running outside Development

Outside Development (`ASPNETCORE_ENVIRONMENT` set to anything but `Development`, or unset), the sample needs more configuration before it starts:

1. **Provide secrets through the environment or a secret store.** User secrets are not loaded. Set at least `ConnectionStrings__BeaconContext` and `Beacon__EncryptionKey`.
2. **Set `Beacon:DataProtection:ApplicationName`.** One name per deployment, for example `beacon-staging`. Without it the host does not start.
3. **Encrypt the Data Protection keys at rest.** Outside Development the host refuses to start while the key ring, which protects sign-in cookies, is stored unencrypted. The sample chains no `ProtectKeysWith*` call: add one to the builder `AddBeaconDataProtection` returns in `Program.cs` (for example `ProtectKeysWithAzureKeyVault` or `ProtectKeysWithCertificate`), or, if the database is protected by other means, set `Beacon:DataProtection:AllowUnencryptedKeys` to `true` and accept the startup warning.
4. **Set `Beacon:UserManagement:SetupToken`** to a secret of at least 32 characters, so the first admin can be created with a token you control, and every replica accepts the same one.
5. **Set `Beacon:PublicBaseUrl` and `Beacon:BaseUrl`** to the public origin and the public UI address (with `/beacon`).
6. **Configure the listening address**, for example `ASPNETCORE_URLS`. Without a launch profile ASP.NET Core does not use the ports above.
7. **Behind a reverse proxy**, set `Beacon:ForwardedHeaders:Enabled` and list the proxy addresses in `KnownProxies`, so the login rate limiter and cookie security see the client's address and scheme.

The [Production checklist](/integrate/production/) covers the rest for a real deployment.

## Building a release

```bash
dotnet publish src/Beacon.SampleProject -c Release -o ./publish
```

The Release build runs `npm ci` (when needed) and `npm run build` for the UI, so the output includes it under `wwwroot/beacon`. Run it with:

```bash
cd publish
ASPNETCORE_ENVIRONMENT=Production dotnet Beacon.SampleProject.dll
```

with the settings from [Running outside Development](#running-outside-development) supplied through the environment.

## URLs

| URL | What |
|---|---|
| `/beacon` | The React UI. The site root `/` belongs to the host. |
| `/beacon/setup` | First-run setup, while no admin exists. |
| `/beacon/login` | Sign-in. |
| `/beacon/api/health` | Health check: `{"status":"ok"}` when the metadata database answers. |
| `/beacon/api/*` | REST API and the SignalR hub (`/beacon/api/hub`). |
| `/beacon/mcp` | MCP server. |
| `/openapi/v1.json` | OpenAPI document. |
| `/warp` | Warp dashboard (Admin only). |

The full list is in [Routes & endpoints](/reference/routes/).

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `Beacon:DataProtection:ApplicationName must be set outside Development` | The host runs outside Development. Use `--launch-profile https` locally, or follow [Running outside Development](#running-outside-development). |
| Startup refuses Data Protection keys "persisted without encryption at rest" | Outside Development without key encryption. See step 3 of [Running outside Development](#running-outside-development). |
| `Beacon:EncryptionKey` errors | The key is missing, too short, printable text or a published sample value. Generate one with `openssl rand -base64 32`. |
| `relation "…" does not exist` | Beacon's schema is not migrated. See [Beacon's database](#beacons-database). |
| `extension "vector" is not available` | Install pgvector on the PostgreSQL server, or use the `pgvector/pgvector` image. |
| `404` under `/beacon` | The UI is not built. See [The UI](#the-ui). |
| Subscriptions never run | Warp is not running or cannot reach PostgreSQL. Check `/warp` and the host log. |

More in [Troubleshooting](/integrate/troubleshooting/).

## Embedding Beacon in your own application

This page covers the sample only. To add Beacon to your own ASP.NET Core application (packages, `Program.cs`, middleware order, migrations at startup, your job runner and your sign-in), follow [Integrate Beacon](/integrate/).
