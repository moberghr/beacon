# Security & Compliance

> §1.x — Auth, secrets, audit, PII. Loaded automatically by Claude Code.

## Secrets

§1.1 **Connection-string encryption is mandatory.** All data-source connection strings are encrypted at rest using `Beacon:EncryptionKey` (required config). NEVER store plaintext connection strings in the database, in entities, in tests, or in fixture files.

§1.2 **No secrets in source.** NEVER hardcode connection strings, API keys, encryption keys, OIDC client secrets, or LLM provider keys in `.cs`, `.razor`, `.json`, or test files. All sensitive values come from `appsettings.json` (gitignored sections), environment variables, or the encrypted store.

## API keys

§1.3 **API keys are SHA256-hashed before storage.** The raw key is shown to the user exactly once at creation and never persisted in plaintext. Never log or echo the raw key.

§1.4 **Scoped keys only.** API keys carry scopes (`Read`, `Execute`, `Admin`) and optional project restrictions — enforce both on every request, not just the scope.

## MCP guardrails

§1.5 **MCP query execution is read-only enforced, defense-in-depth.** Regex guardrail → dialect-aware `SqlReadOnlyAstValidator` → `ExecuteReadOnlyQueryAsync`, which on PostgreSQL runs the statement inside a `READ ONLY` transaction (plus `default_transaction_read_only`) and on MySQL inside `START TRANSACTION READ ONLY` (plus `SET SESSION TRANSACTION READ ONLY`) on a connection with statement batches off. The validator's dialect comes from `DataSourceSqlDialect.Of(dataSource)` (BigQuery/Databricks by data source type); an unknown or null dialect is refused, never parsed with a generic grammar. MCP call sites (`ProjectQueryTool`, `QueryExecutionService`, `CrossSourceQueryService`, `DryRunTool`, `McpEvalService`) MUST use the read-only variant; non-MCP call sites are unaffected. `IDataSourceProvider.SupportsDatabaseReadOnlyEnforcement` reports honestly (PostgreSQL and MySQL today; SQL Server, Synapse, Snowflake, BigQuery and Databricks rely on the parser gates and a read-only login) — do not claim coverage a provider does not have. Do not add a code path that bypasses these checks, even for "trusted" sessions. A cross-database stage follows the AST validator: `SqlCrossDatabaseRule.Validate(sql, dialect, configuredDatabase)` refuses any name qualified with a database (catalog, project) other than the one the data source's connection is configured for (`IConfiguredDatabaseResolver`; none configured or unreadable → no qualifier is accepted), functions and procedures that reach other servers, files or storage, Databricks paths, statements that switch database, catalog, schema, role or user (`USE`, `SET ROLE`, `EXECUTE AS`, …) or run SQL from a string, variable or session (`EXEC @sql`, `sp_executesql`, `EXECUTE IMMEDIATE`, `PREPARE`/`EXECUTE`, string-bodied functions), and SQL it cannot parse — it runs in `ISqlExecutionGate` (verdict `cross_database`, also with read-only enforcement off; skipped only for the in-memory join, `SqlGateRequest.ForInMemoryJoin`), on query save and on step execution, and a new execution path must not skip it. It is not applied by `IDataSourceProvider.Execute*` called directly, nor by Beacon's own metadata reads (extractors, samplers, value probes).

§1.6 **PII detection and row limits stay on.** Both are configurable per project; do not disable them in code or default config without explicit approval.

§1.7 **Audit logging is non-optional.** Every MCP tool invocation goes through `McpAuditService`, including the error path — never short-circuit it. Learning signals (`McpSignalService`) are recorded for the SQL-carrying tools only; see §9.5.

§1.12 **Grounding retrieval is project-scoped, and scoping is authorization.** Glossary terms, golden exemplars, learned patterns, and replay eval cases are filtered by the caller's authorized `projectId` — NOT by data source. A data source shared across projects must never leak one project's questions, SQL, or definitions into another's prompt. Any new retrieval block must thread the authorized `projectId` through and hard-filter on it.

## Project authorization

§1.13 **Project access has one decider: `IProjectAccessEvaluator`.** Never re-derive it in a handler from `allowed_projects`, roles or membership rows. Every MediatR request is exactly one of: `IProjectAuthorizedRequest` (its `ProjectChecks` name every id it addresses and the `ProjectAction`; `ProjectAuthorizationBehavior` answers 404 unreadable / 403 denied before the handler), `IProjectFilteredRequest` (filters in SQL through `ProjectSet` / `ProjectAccessQueryableExtensions`, never in memory after paging), or `[ProjectScopeExempt("reason")]`, gated by the policy it names. A resource that reads several data sources is reachable only through one project that holds all of them; a query without steps only by its owner (`Query.OwnerUserId`) and Admins, and an empty source set never counts as open. Refuse with `BeaconForbiddenException` (403) / `BeaconNotFoundException` (404) through `AccessRefusal` / `ProjectAccessGuard` (ids-only security events) — never `UnauthorizedAccessException`, which the API does not map. API keys never carry the Admin bypass and are capped by the lower of their scope and their owner's global role; a session without a stored user gets nothing while user management is on. Off-request code gets the system scope only in a scope marked through `ISystemWorkScope` (a job host marks each job's scope); an unmarked scope without a request gets nothing (fail closed) and logs a warning, so every new off-request entry point marks its scope first; a path that must never run as the system (the playground) refuses explicitly. Scheduled work done for a user (subscription runs, contract evaluations, AI actor background cycles) runs as its recorded owner: the job entry point sets `IRunAsScope` before anything resolves the scope's caller, and `IExecutionAccessCheck` re-checks the exact data sources right before any SQL or LLM call; a refused run is recorded as skipped, never run as the system. MCP takes the caller's projects from the evaluator on every call (`ProjectContextFactory.PopulateAsync`, run by the MCP request filters); a null or empty project list denies, and access that could not be determined (`AccessUndetermined`) answers a retry message and fails `tools/list`, never a shorter list. New projects are restricted; the creator becomes ProjectAdmin (open only when Beacon cannot manage membership for the creator, logged as `ProjectCreatedOpen`). Check-then-write rules on memberships and links run in a serializable transaction and map serialization conflicts to 409. Users are named to other users by display name or "User {id}" (`UserLabel`), never by user name, e-mail or external id. Functional permissions (`BeaconPermissions`: Approver, Auditor, SecurityAdmin, UserAdmin, DataSourceAdmin, MigrationOperator) grant no project data: the evaluator's bypass stays bound to Admin, a duty's grant goes through `ProjectCheck.GrantedBy` or its handler (read live through `IBeaconPermissionAccessor`), and an API key never holds one. Only an Admin controls how an account signs in (creating password accounts, changing user names, e-mails or external ids); a UserAdmin cannot.

## Auth middleware

§1.8 **Cookie config:** `Beacon.Auth` cookie uses `HttpOnly = true`, `SameSite = Lax`, `SecurePolicy = SameAsRequest`. Do not weaken any of these flags.

§1.9 **Middleware order is load-bearing:** `ApiKeyAuthMiddleware` → `JwtBearerAuthMiddleware` → `UseAuthentication` → `BeaconCookieAuthMiddleware` → `UseAuthorization` → `LoginFormAuthMiddleware`. `BeaconCookieAuthMiddleware` must populate `context.User` from the `Beacon.Auth` cookie BEFORE `UseAuthorization` evaluates policies, otherwise the first authorization check on a cookie session sees an unauthenticated user. Reordering also breaks API-key-only callers and the login redirect.

## SQL safety

§1.10 **Parameterize every Dapper / raw SQL query.** Never interpolate user input into SQL strings — even for column or table names, use a whitelist + parameter pattern.

## Logging

§1.11 **No PII in logs.** User-supplied query text, connection strings, full row payloads, and auth tokens NEVER reach `ILogger`. Log identifiers and counts only.
