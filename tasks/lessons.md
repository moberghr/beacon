# Lessons Learned

## NSwag generates intentionally loose types — local strict result interfaces are deliberate (2026-06-02)

**What happened:** A "refactor" to delete duplicated result interfaces and import generated types (to drop ~92 `as unknown as` casts) was premised on the casts being gratuitous. They are not.

**Rule:** Do NOT replace hand-written result/command interfaces in `Beacon.UI/web/src/routes/**/queries.ts` with imports from `src/api/generated/beacon-api.ts`. The NSwag config (`nswag.config.json`) sets `markOptionalProperties: true`, emits a `[key: string]: any` index signature on all 223 interfaces, and types `DateTime` as `Date` even though the client deserializes with a plain `JSON.parse` (no reviver) so dates are strings at runtime. The local interfaces are stricter and more correct.

**Instead:** bridge the loose generated payload into the strict local type at the call boundary via `unwrap<T>()` in `src/lib/api.ts` (the single, greppable trust boundary; add zod here later if needed). The "92 casts" were 5 unrelated categories — only the ~40 named-result-type double-casts are the addressable ones; `as never` command args, react-hook-form `register('x' as never)`, Monaco, and Date-field casts are legitimate and unrelated.

**Why it matters:** Importing the generated types would explode `tsc` errors (optional everywhere, Date vs string) and *reduce* type quality. Verify codegen output shape before assuming duplication is debt.

**When it applies:** Any time generated-client types look "duplicated" by local interfaces in this repo.
