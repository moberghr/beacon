# Phase 3 Batch 5c — Multi-step AddSubscription + AddRecipients

**Branch:** `feat/react-phase3` (PR #13 draft)
**Slot rule:** Heavy slot — focused on form complexity (multi-step RHF stepper) rather than a new page.

---

## Why two pieces in one slot

Both flows need the same primitive (visual stepper + per-step Zod validation + back/next/finish). Building it once and reusing it is cleaner than splitting across slots.

---

## Reusable infra

### `web/src/components/ui/Stepper.tsx`
Visual step bar (pure presentation). Renders numbered bullets with `--active` / `--done` states, optional click handler that lets the user jump back to completed steps.

### `web/src/components/ui/StepperDialog.tsx`
Generic-over-RHF wrapper around `Dialog`. Owns the active-step index. Each step declares `fields: Path<TForm>[]` so `goNext` calls `form.trigger(stepFields)` before advancing — invalid fields stay highlighted, navigation is blocked. Final-step Next becomes a configurable submit button (`finishLabel`). Emits `data-testid="stepper-next"` for tests.

### CSS additions in `styles-beacon.css`
- `.stepper`, `.stepper__group`, `.stepper__step`, `.stepper__step--active`, `.stepper__step--done`, `.stepper__step-bullet`, `.stepper__step-text`, `.stepper__step-title`, `.stepper__step-desc`, `.stepper__divider`, `.stepper__review`.

---

## AddSubscriptionDialog (rewritten)

`web/src/routes/subscriptions/AddSubscriptionDialog.tsx` — replaces Batch 4's single-form version.

3 steps:
1. **Query** — query id (numeric) + cron expression. Fields validated: `queryId`, `cronExpression`.
2. **Recipients** — multi-select from `useRecipientsQuery()`, optional `maxRows`/`timeoutSeconds`, 4 boolean delivery options. Fields validated: `recipientIds` (min 1).
3. **Review** — read-only summary in `<dl class="stepper__review">`. Submit creates subscription via `useCreateSubscription`.

`useCreateSubscription` invalidates and toasts as before. `ApiError` is unwrapped to a friendly message in `onFinish`.

---

## AddRecipientsDialog (refactored in `RecipientsTab.tsx`)

`RecipientPicker` (used from Subscription detail's Recipients tab) is now 2-step:
1. **Select** — search box + filtered checkbox grid of candidates (existing recipients minus those already attached).
2. **Review** — confirms which recipients will be attached, with destination + notification-type pills. Submit calls `useAddSubscriptionRecipients`.

The dialog uses the visual `Stepper` directly (not `StepperDialog`) because the form is plain `useState` (no RHF needed for a multi-select set).

---

## Tests

Updated `AddSubscriptionDialog.test.tsx` to walk steps:
- POST happy path: fill query id → Next → check recipient → Next → "Create subscription".
- Validation block: try advancing the Recipients step with empty selection → step-2 stays open + Zod error renders.

Vitest count: 7 → 7 (rewrite, not new file).

---

## Acceptance gate

| Gate | Result |
|---|---|
| `dotnet build -c Release --property WarningLevel=0` | green |
| `dotnet test` | 35/35 (no backend changes) |
| `npm run build` | green |
| `npm test` | 7/7 |
| `wwwroot/app/` synced | yes (via dotnet build target) |

---

## Files touched

**New:**
- `Beacon.SampleProject/web/src/components/ui/Stepper.tsx`
- `Beacon.SampleProject/web/src/components/ui/StepperDialog.tsx`

**Modified:**
- `Beacon.SampleProject/web/src/routes/subscriptions/AddSubscriptionDialog.tsx` (full rewrite as 3-step)
- `Beacon.SampleProject/web/src/routes/subscriptions/AddSubscriptionDialog.test.tsx` (rewrite for steps)
- `Beacon.SampleProject/web/src/routes/subscriptions/parts/RecipientsTab.tsx` (RecipientPicker → 2-step)
- `Beacon.SampleProject/web/src/styles-beacon.css` (stepper styles)

---

## Deferred

- "Add new recipient inline" inside the AddRecipientsDialog Step 1 (design called for OR). Today the user must close the dialog and visit Recipients → Add. Wiring an inline create is a follow-up — the existing `useCreateRecipient` hook already exists.
- Live cron-preview (next-fire-time) inside Step 1 of AddSubscriptionDialog — the SubscriptionDetail page already shows it server-side; the dialog could fetch the same endpoint on cron change.
- No backend changes — every endpoint reused from prior batches.
