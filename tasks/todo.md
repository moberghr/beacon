# Todo — SSO for Login & MCP Auth

**Spec:** `docs/specs/2026-04-15-sso-login-and-mcp-auth.md`
**Plan:** `docs/plans/2026-04-15-sso-login-and-mcp-auth.md`
**Status:** Implementation complete (batches 1–6). Pending review (Phase 3.5–7).

---

## Batch 1 — Entity + migrations + query-translation test

- [x] Add `IdentityProvider` (nullable string) to `Beacon.Core/Data/Entities/BeaconUser.cs`
- [x] Configure composite non-unique index `(IdentityProvider, ExternalId)` in `Beacon.Core/Data/BeaconContext.cs`
- [x] Add `GetUserByExternalIdAndProviderAsync` to `IUserManagementService` + `UserManagementService`
- [x] Generate PostgreSQL migration `AddIdentityProvider` (`20260415135907`)
- [x] Generate SQL Server migration `AddIdentityProvider` (`20260415140019`)
- [x] Add `GetUserByExternalIdAndProviderQuery_Translates` + `_WithNullProvider_Translates` to `QueryTranslationTests`
- [x] Checkpoint: build green, 6 query tests pass, both migration files exist

## Batch 2 — OIDC options + service-extension wiring

- [x] Create `Beacon.Core/Authentication/OidcAuthenticationOptions.cs`
- [x] Add `Oidc` property to `AuthenticationOptions`
- [x] Implement `AddBeaconOidcAuthentication(services, configuration)` in `Beacon.UI/ServiceExtensions.cs`
- [x] Add `Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.6 NuGet package to `Beacon.UI`
- [x] Wire into `Beacon.SampleProject/Program.cs`
- [x] Add commented OIDC sample block to `appsettings.json`
- [x] Checkpoint: build green; app starts unchanged with OIDC disabled

## Batch 3 — JIT + claims enrichment handler

- [x] Add `GetOrCreateExternalUserAsync` to `IUserManagementService` + `UserManagementService`
- [x] Create `Beacon.UI/Authentication/OidcEventHandlers.cs` with `HandleTokenValidatedAsync` + `HandleRemoteFailureAsync`
- [x] Wire handler into `AddBeaconOidcAuthentication` (Events.OnTokenValidated + OnRemoteFailure)
- [x] Add `InternalsVisibleTo("Beacon.Tests")` to `Beacon.UI.csproj`
- [x] Create `Beacon.Tests/Unit/OidcEventHandlersTests.cs` (4 tests: unknown sub, no sub, disabled user, username fallback)
- [x] Checkpoint: build green; 4 unit tests pass

## Batch 4 — Challenge endpoint + SSO button UI

- [x] Add `GET /api/auth/sso/challenge` in `LoginEndpoints.cs` with `IsSafeReturnUrl` open-redirect guard
- [x] LoginFormAuthMiddleware already allows `/api/auth/` — no change needed. OIDC callback runs at root (before `/beacon` branch) — no change needed.
- [x] Create `Beacon.UI/Components/Shared/SsoLoginButton.razor`
- [x] Render `<SsoLoginButton />` above password form in `Login.razor`
- [x] Add `ssoError` query param detection in `Login.razor` for OIDC failure feedback
- [x] Create `Beacon.Tests/Unit/SsoChallengeReturnUrlTests.cs` (16 parameterized tests for open-redirect guard)
- [x] Checkpoint: build green; 20 unit tests pass

## Batch 5 — Promote JwtBearer middleware to root pipeline for MCP

- [x] Add `UseBeaconJwtBearerAuthentication()` public extension in `ServiceExtensions.cs` (wraps internal middleware)
- [x] Call `UseBeaconJwtBearerAuthentication()` at root pipeline in `Program.cs` (after `ApiKeyAuthMiddleware`)
- [x] Auto-populate `JwtAuthenticationOptions` from `Oidc.Authority` when OIDC is enabled
- [x] McpBearerJwtTests deferred to integration-test follow-up (needs WebApplicationFactory + mock JWKS)
- [x] Checkpoint: build green; 28 tests pass; API-key flow unchanged

## Batch 6 — Behavioral diff + gitignore

- [x] `.gitignore` already updated with `docs/specs/` and `docs/plans/` (done in Phase 2)
- [x] Behavioral diff written in `docs/plans/2026-04-15-sso-login-and-mcp-auth.md`
- [ ] Full mock-OIDC integration tests (SsoLoginFlowTests, McpBearerJwtTests) — follow-up slice
- [x] Checkpoint: full `dotnet test` passes (28/28)

## Post-implementation review (Phase 3.5 → 7)

- [ ] Phase 3.5: run spec-drift-detection against `2026-04-15-sso-login-and-mcp-auth.json`
- [ ] Phase 4 Stage 1: `mtk:compliance-reviewer`
- [ ] Phase 4 Stage 2: `mtk:test-reviewer` + `mtk:architecture-reviewer`
- [ ] Phase 5: fix review findings (≤ 3 iterations)
- [ ] Phase 6: `code-simplification` sweep on new files
- [ ] Phase 7: append lessons, update `CLAUDE.md` / `pre-commit-review-list.md` if warranted
