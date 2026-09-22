# Optional, self-wiring SignalR in Beacon.Api (2026-09-22)

Spec: `docs/specs/2026-09-22-optional-signalr.md` · Plan: `docs/plans/2026-09-22-optional-signalr.md`
Rigor: MAX (score 13)

## B1 — Library wiring
- [ ] `BeaconApiOptions` (public): `Realtime = true`, `ConfigureSignalR`
- [ ] `RealtimeDisabledApprovalNotifier` — explicitly named, logs once at Debug, never a silent swallow
- [ ] `AddBeaconApiServices(Action<BeaconApiOptions>?)`; no-arg overload delegates to it
- [ ] Realtime on: `AddSignalR()` + `ConfigureSignalR` + `TryAddSingleton<IUserIdProvider>` + `TryAddScoped` SignalR notifier
- [ ] Realtime off: `TryAddScoped` disabled notifier only, no SignalR registration
- [ ] `MapBeaconApi()` maps `/beacon/api/hub` with `AuthPolicyName` only when realtime is on
- [ ] Tests: 7 DI/mapping cases incl. `HostAddSignalRFirst_StillResolves` (proves the idempotency assumption)
- [ ] Checkpoint: `dotnet build --property WarningLevel=0` + `dotnet test`

## B2 — Advertise the flag
- [ ] `CurrentUserResponse.RealtimeEnabled`; `Anonymous` static becomes a factory taking the flag
- [ ] `/auth/me` reads the registered options
- [ ] Checkpoint: build + test

## B3 — Sample host
- [ ] Drop `AddSignalR()` (Program.cs:217) and `MapHub<BeaconHub>(...)` (:332)
- [ ] Verify `JobStatusChangedBehavior` still resolves `IHubContext<BeaconHub>`
- [ ] Checkpoint: build + test; host boots

## B4 — SPA honours the flag
- [ ] `npm run codegen` — diff must be confined to the `/auth/me` shape
- [ ] `CurrentUser.realtimeEnabled` in `useAuth.ts`
- [ ] `useHubEvent` / `useHubReconnected` skip `addSubscriber` when the flag is false
- [ ] MSW `/auth/me` handler gains the field
- [ ] Vitest: no connection attempted when disabled
- [ ] Checkpoint: `npm run build` + `npx vitest run`

## B5 — Docs (mechanical, inline)
- [x] README: endpoint table (:117), embedding section (:345-390), canonical-reference note (:388)
- [x] `getting-started/installation.md` — stale "SignalR plumbing" comment (~:197)
- [x] `getting-started/configuration.md` — stale "SignalR plumbing" comment (~:22, :59)
- [x] `features/notifications.md:551` + `features/ai-actors.md:169` — note the opt-out
- [x] Default (free realtime) and opt-out both obvious at a glance

## Post-implementation review
- [ ] Phase 3.5 spec-drift check against the JSON sidecar
- [ ] Stage 1 — `compliance-reviewer` against the sealed spec
- [ ] Stage 2 (MAX) — `test-reviewer` + `architecture-reviewer` + `silent-failure-hunter`
- [ ] Confirm no pre-existing-failure delta vs the Phase 2.9 baseline

## Follow-ups raised by the same audit (NOT in this run)
- [ ] README quick-start symbols (`AddBeacon` → `AddBeaconServices`, `UseSqlServer` chaining, `UseBeaconUI` → `MapBeaconUi`) — both provider READMEs
- [ ] `AddBeaconApiServices()` should register the `BeaconApi` policy
- [ ] Semantico 3.7.0.1 → 4.1.0 upgrade path (SQL script + `[Obsolete]` shim + changelog for `IBeaconScheduler`/`IJobService` breaks)
- [ ] `Beacon:EncryptionKey` error message should name `Semantico:EncryptionKey`
- [ ] SPA sub-path hosting (configurable `<base href>`)
- [ ] Host should read `Beacon:Schema` instead of hardcoding `"semantico"`
