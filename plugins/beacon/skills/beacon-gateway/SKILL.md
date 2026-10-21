---
name: beacon-gateway
description: Add an MCP gateway to an existing ASP.NET Core application with Moberg.Beacon.Gateway, so AI clients (Claude, Claude Desktop, Cowork, Claude Code) act as the application's own signed-in users through its own endpoints. Use when someone asks to expose their .NET app over MCP, connect Claude to their application, or add Beacon as an MCP server for their own endpoints — not for Beacon's data product (data sources, SQL), which follows llms-integrate.txt.
---

# Add a Beacon MCP gateway

The recipe lives in Beacon's docs, and Beacon checks most mistakes at startup with a message that says the fix. Follow the
guide, not memory:

1. **Read the guide first:** fetch `https://moberghr.github.io/beacon/llms-gateway.txt` and follow it in order.
2. **Read the application before deciding anything.** Find and note:
   - sign-in: `AddMicrosoftIdentityWebApp` / `AddAuthentication(...)` and its default scheme, the `AzureAd` section;
   - the users table, its Entra object id column, its active flag, and whether that column is unique;
   - the database: an `NpgsqlDataSource` (Aspire `AddNpgsqlDataSource` / `AddAzureNpgsqlDataSource`), an EF context, or SQL Server;
   - a migrator project or deploy step (Beacon's schema is migrated there, never at web startup);
   - endpoint style: minimal APIs, controllers, or handler classes (Moberg Warp `[WarpHttp*]`);
   - any `IClaimsTransformation` (it must be registered before `AddBeaconMcpGateway`);
   - the authorization policies (one becomes the admin UI's `HostPolicy`);
   - `Directory.Packages.props` (central package versions).
3. **Ask the decisions the guide lists** (who may connect; self-service only or the person's browser permissions; which
   endpoints; admin UI and its policy; where Beacon's tables live). Propose the defaults, never assume them.
4. **Wire it:** the packages, an `IMcpHostUserResolver` (lookup by object id, exactly one active user or nobody, never
   provisioning), the `AddBeaconMcpGateway` block, `UseBeaconMcpGateway()` before `UseAuthentication()`,
   `MapBeaconMcpGateway()`, and the migrator call (`BeaconPostgreSqlMigrator` / `BeaconSqlServerMigrator`). Put
   `Beacon:EncryptionKey` in user secrets (`openssl rand -base64 32`), never in a committed file.
5. **Self-service only?** Make the application's own permission check give an agent call nothing beyond the person's own
   records: `principal.IsBeaconMcpCall()` is true for a tool call.
6. **Mark the tools** with the `beacon-tool` skill, one endpoint at a time.
7. **Test it** with `Moberg.Beacon.Testing` (`McpTestTokens`, `McpTestUsers`, `McpTestClient`): pin which endpoints are
   tools, that each refuses somebody else's data, and that agent calls are self-service. Build and run the whole suite.
8. **Entra:** the `beacon-entra` skill prints the registration steps for the person to run.
9. **After deploying:** `dotnet tool install -g Moberg.Beacon.Doctor` then `beacon-doctor mcp https://<host>/beacon/mcp`
   (add `--token` with a real access token to check the handshake). Fix every FAIL before connecting Claude.

Stop and ask before anything outward-facing: changing the Entra tenant, pushing, deploying.
