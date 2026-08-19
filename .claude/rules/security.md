# Security & Compliance

> §1.x — Auth, secrets, audit, PII. Loaded automatically by Claude Code.

## Secrets

§1.1 **Connection-string encryption is mandatory.** All data-source connection strings are encrypted at rest using `Beacon:EncryptionKey` (required config). NEVER store plaintext connection strings in the database, in entities, in tests, or in fixture files.

§1.2 **No secrets in source.** NEVER hardcode connection strings, API keys, encryption keys, OIDC client secrets, or LLM provider keys in `.cs`, `.razor`, `.json`, or test files. All sensitive values come from `appsettings.json` (gitignored sections), environment variables, or the encrypted store.

## API keys

§1.3 **API keys are SHA256-hashed before storage.** The raw key is shown to the user exactly once at creation and never persisted in plaintext. Never log or echo the raw key.

§1.4 **Scoped keys only.** API keys carry scopes (`Read`, `Execute`, `Admin`) and optional project restrictions — enforce both on every request, not just the scope.

## MCP guardrails

§1.5 **MCP query execution is read-only enforced, defense-in-depth.** Regex guardrail → dialect-aware `SqlReadOnlyAstValidator` → on PostgreSQL, `ExecuteReadOnlyQueryAsync` runs the statement inside a `READ ONLY` transaction. MCP call sites (`ProjectQueryTool`, `QueryExecutionService`, `CrossSourceQueryService`, `DryRunTool`, `McpEvalService`) MUST use the read-only variant; non-MCP call sites are unaffected. `IDataSourceProvider.SupportsDatabaseReadOnlyEnforcement` reports honestly (PostgreSQL only today) — do not claim coverage a provider does not have. Do not add a code path that bypasses these checks, even for "trusted" sessions.

§1.6 **PII detection and row limits stay on.** Both are configurable per project; do not disable them in code or default config without explicit approval.

§1.7 **Audit logging is non-optional.** Every MCP tool invocation goes through `McpAuditService`, including the error path — never short-circuit it. Learning signals (`McpSignalService`) are recorded for the SQL-carrying tools only; see §9.5.

§1.12 **Grounding retrieval is project-scoped, and scoping is authorization.** Glossary terms, golden exemplars, learned patterns, and replay eval cases are filtered by the caller's authorized `projectId` — NOT by data source. A data source shared across projects must never leak one project's questions, SQL, or definitions into another's prompt. Any new retrieval block must thread the authorized `projectId` through and hard-filter on it.

## Auth middleware

§1.8 **Cookie config:** `Beacon.Auth` cookie uses `HttpOnly = true`, `SameSite = Lax`, `SecurePolicy = SameAsRequest`. Do not weaken any of these flags.

§1.9 **Middleware order is load-bearing:** `ApiKeyAuthMiddleware` → `JwtBearerAuthMiddleware` → `UseAuthentication` → `BeaconCookieAuthMiddleware` → `UseAuthorization` → `LoginFormAuthMiddleware`. `BeaconCookieAuthMiddleware` must populate `context.User` from the `Beacon.Auth` cookie BEFORE `UseAuthorization` evaluates policies, otherwise the first authorization check on a cookie session sees an unauthenticated user. Reordering also breaks API-key-only callers and the login redirect.

## SQL safety

§1.10 **Parameterize every Dapper / raw SQL query.** Never interpolate user input into SQL strings — even for column or table names, use a whitelist + parameter pattern.

## Logging

§1.11 **No PII in logs.** User-supplied query text, connection strings, full row payloads, and auth tokens NEVER reach `ILogger`. Log identifiers and counts only.
