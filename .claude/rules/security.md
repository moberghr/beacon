# Security & Compliance

> §1.x — Auth, secrets, audit, PII. Loaded automatically by Claude Code.

## Secrets

§1.1 **Connection-string encryption is mandatory.** All data-source connection strings are encrypted at rest using `Beacon:EncryptionKey` (required config). NEVER store plaintext connection strings in the database, in entities, in tests, or in fixture files.

§1.2 **No secrets in source.** NEVER hardcode connection strings, API keys, encryption keys, OIDC client secrets, or LLM provider keys in `.cs`, `.razor`, `.json`, or test files. All sensitive values come from `appsettings.json` (gitignored sections), environment variables, or the encrypted store.

## API keys

§1.3 **API keys are SHA256-hashed before storage.** The raw key is shown to the user exactly once at creation and never persisted in plaintext. Never log or echo the raw key.

§1.4 **Scoped keys only.** API keys carry scopes (`Read`, `Execute`, `Admin`) and optional project restrictions — enforce both on every request, not just the scope.

## MCP guardrails

§1.5 **MCP query execution is read-only enforced.** All MCP-executed SQL is read-only at the connector level. Do not add a code path that bypasses the read-only check, even for "trusted" sessions.

§1.6 **PII detection and row limits stay on.** Both are configurable per project; do not disable them in code or default config without explicit approval.

§1.7 **Audit + signal logging is non-optional.** Every MCP tool invocation goes through `McpAuditService` and `McpSignalService`. Do not short-circuit either, even on the error path.

## Auth middleware

§1.8 **Cookie config:** `Beacon.Auth` cookie uses `HttpOnly = true`, `SameSite = Lax`, `SecurePolicy = SameAsRequest`. Do not weaken any of these flags.

§1.9 **Middleware order is load-bearing:** `ApiKeyAuthMiddleware` → `UseAuthentication` → `UseAuthorization` → `BeaconCookieAuthMiddleware` → `LoginFormAuthMiddleware`. Reordering breaks API-key-only callers and the Blazor login redirect.

## SQL safety

§1.10 **Parameterize every Dapper / raw SQL query.** Never interpolate user input into SQL strings — even for column or table names, use a whitelist + parameter pattern.

## Logging

§1.11 **No PII in logs.** User-supplied query text, connection strings, full row payloads, and auth tokens NEVER reach `ILogger`. Log identifiers and counts only.
