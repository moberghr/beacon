# Phase 0 security slice — release 4.5.1 (2026-10-08)

Spec: `docs/specs/2026-10-08-phase0-security-slice.md` · Plan: `docs/plans/2026-10-08-phase0-security-slice.md`
Branch: `fix/phase0-security-slice` (from origin/main 944793bb)
Rigor: MAX (score 33 — 7 batches, 57 files, security_impact=new-auth-path)

## B1 — SQLite final-query gate (0.1)
- [x] Tests first: InMemoryDatabaseManagerTests (ATTACH blocked, paged callback), VirtualTableManagerTests (paged ATTACH/CREATE/zero-step rejected before load), QueryServiceReadOnlyGateTests (create/update FinalQuery)
- [x] `SQLITE_LIMIT_ATTACHED = 0`; `ExecutePagedAsync(validate)` gates the original statement; static `TranslateResultReferences`
- [x] Paged final query validated before loading; save paths validate FinalQuery
- [x] Checkpoint: build + tests

## B2 — ReDoS + input caps (0.3)
- [x] Tests first: QueryGuardrailServiceTests (128 KB < 1 s, stacked write rejected, timeout fail-closed), SqlExecutionGateTests (SqlTooLong), McpInputLimitsTests (new), ProjectAskToolRepairFlowTests (long question)
- [x] Linear DangerousPattern + timeouts + IsMatchFailClosed seam; atomic LeadingSelectPattern
- [x] MaxSqlChars / MaxQuestionChars options + validation; gate length cap; ask question cap
- [x] Checkpoint: build + tests

## B3 — PII masking by result column (0.4)
- [x] Tests first: PiiRowMaskerTests (new); SELECT * / alias / customer_email / email_address on query, ask, cross-source, saved-query surfaces
- [x] Static `PiiRowMasker.Mask`; replace 4 masking blocks; ReadOnlyExecutionRoutingTests green unmodified
- [x] Checkpoint: build + tests

## B4 — Permission enforcement + disabled-user keys (0.2, 0.17)
- [x] Tests first: BeaconPermissionEndpointFilterTests, AuthProviderRegistrationTests, DatabaseAuthorizationProviderIdentityTests, ApiKeyServiceValidationTests (new)
- [x] Default* providers after user-management block; DatabaseAuthorizationProvider resolves API-key (username) and cookie/OIDC (NameIdentifier) callers
- [x] BeaconPermissionEndpointFilter + AllowViewerAccess on 3 endpoints; delete BeaconAuthorizationMiddleware
- [x] ApiKeyService rejects disabled users
- [x] Checkpoint: build + tests + OpenApiContractTests + Phase1HarnessTests (vs baseline)

## B5 — Login rate limit (0.7)
- [x] Tests first: LoginEndpointsTests (11th attempt → 429 without host limiter; per-IP; host limiter without policy still works)
- [x] LoginRateLimiter + filter; registered in AddBeaconApiServices; sample policy removed
- [x] Checkpoint: build + tests

## B6 — AI actors propose-only (0.5)
- [x] Tests first: AiActorActionGuardTests, AiActorServiceProposeOnlyTests (new)
- [x] AiActorActionGuard; ExecuteOrProposeAsync in 3 loops; archive owner/lock, create caps; `Proposed` flag; docs
- [x] Checkpoint: build + tests

## B7 — Project scoping + MCP early-return audit (0.9, 0.11)
- [x] Tests first: LearnedPatternProjectScopingTests, McpToolEarlyReturnAuditTests (new)
- [x] ProjectId filter at both pattern sites; drop unscoped doc arm of SearchAsync
- [x] Audit every early return in get_context / search / get_documentation
- [x] Checkpoint: build + tests

## Review
- [x] Full build + test vs baseline (1760 → 2093)
- [x] Behavioural diff / release notes
- [x] Spec-drift check
- [x] Stage 1 compliance review; Stage 2 test + architecture + silent-failure lanes
- [x] Fix findings (3 iterations + 1 post-cap PII fix); cleanup; lessons
