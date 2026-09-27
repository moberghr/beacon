---
title: Saved Query Tools
description: Expose approved, parameterized saved queries as named, read-only MCP tools such as q_loan_book_by_month(from, to), so agents and routines call reviewed, versioned SQL instead of carrying their own.
---

An agent that writes its own SQL for a recurring question gets a slightly different query every time. A **saved query
tool** turns a reviewed Beacon query into a fixed MCP tool — `q_loan_book_by_month(from, to)` — that always runs the
same approved SQL with the caller's arguments bound as database parameters. Routines become deterministic and
cheaper: they call a tool instead of shipping `queries/*.sql` files or asking `ask` to regenerate SQL.

## Expose a query

1. Write the query as usual. Parameters are `{name}` placeholders in the step SQL, with a type (Number, DateTime,
   String) and a description — the description becomes the argument's description in the tool schema.
2. Get a version **approved** (see [the approval rule](#which-version-runs)). With the approval workflow enabled,
   saving SQL changes submits a version for approval; an admin approves it on the Approvals page.
3. On the query's detail page, an **admin** opens the **MCP tool** card, ticks *Expose as MCP tool*, sets a name
   and (optionally) a description, and saves. The card shows which approved version calls run, or why none can.

The same through the API (Admin policy):

```http
PUT /beacon/api/queries/42/mcp-tool
Content-Type: application/json

{ "enabled": true, "name": "loan_book_by_month", "description": "Outstanding loan book per month for a date range." }
```

Rules the endpoint enforces:

| Field | Rule |
| --- | --- |
| `name` | `^[a-z][a-z0-9_]{2,40}$`; the MCP tool is `q_<name>`. Unique across live (non-archived) queries — so it is unique in every project and always names one query. Required to enable. Two concurrent saves of the same name get the same "already used" error (`400`), never a `500`. |
| `description` | Optional, at most 1000 characters. Falls back to the approved version's description, then its name. |
| `enabled: true` | Requires a runnable approved version whose parameters make a valid tool (see below). Disabling is always allowed; the name stays reserved while it is set. Disabling with a name another query already uses succeeds and keeps the query's stored name. |

The query detail DTO (`GET /beacon/api/queries/{id}`) carries `mcpToolEnabled`, `mcpToolName`,
`mcpToolDescription`, `mcpToolRunnableVersionNumber` and `mcpToolIssue`. `mcpToolIssue` explains why the query is not
a tool anyone can call: no approved active version, a parameter shape that cannot be a tool, or **not visible in any
project** — no single project contains all of the data sources its steps read, so no caller can ever see it. The
last one does not block enabling (a data source may be added to a project later); it is reported so the tool does
not silently stay invisible.

## Which version runs

A tool runs **only** the query's active version, and only when an approval request for **exactly that version** was
approved. Nothing else is callable:

| Version state | Callable? |
| --- | --- |
| Active, its approval request Approved | **Yes** — this is the only runnable version |
| Pending approval (a later edit) | No — the tool keeps running the approved version until the edit is approved |
| Rejected | No |
| Active without an approval (edited while the approval workflow was off, or restored from history) | No |
| Draft / no version | No |

A query with no runnable version is **not listed** as a tool at all, even when enabled. The tool executes the
approved version's **snapshot** (its stored steps and final query), not the live step rows, so an unreviewed change
to the live query can never change what the tool runs. In practice: enable the approval workflow
(`options.ApprovalWorkflow.Enabled = true`) on hosts that use saved-query tools.

A version is also not callable when its parameters cannot form a tool: a parameter without a placeholder, a name
that is not a plain identifier, the reserved name `project_id`, or the same parameter declared with different types
in different steps. The MCP tool card shows the reason.

## What the agent sees

Each tool is listed with:

- **name** `q_<name>`, title (the query name) and the description, plus the approved version number;
- an **input schema** built from the parameters: `number` for Number, an ISO 8601 `string` for DateTime, a
  `string` (max 4000 characters) for String; every parameter is `required` (the query model has no defaults) and
  `additionalProperties` is false;
- annotations `readOnlyHint: true`, `destructiveHint: false`, `idempotentHint: true`, `openWorldHint: false`.

Arguments are validated before anything runs. A missing or unknown argument, a non-number for a Number, or a date
that is not ISO 8601 is a tool error. Dates with `Z` or an offset are normalized to UTC; a bare date
(`2026-01-31`) is passed as written.

The result is a markdown table plus structured content:

```json
{
  "columns": [{ "name": "month", "type": "string" }, { "name": "principal", "type": "number" }],
  "rows": [{ "month": "2026-01", "principal": 1250000.00 }],
  "rowCount": 1,
  "truncated": false,
  "queryVersionId": 318,
  "versionNumber": 4
}
```

`truncated` is true when the result had more rows than the project's MCP row limit (or rows were dropped to keep the
payload within 256 KB, then `rowsOmittedForSize` is also true).

**Many tools.** Up to `SavedQueryTools.NamedToolLimit` tools (default 25) visible to one caller, each is its own
`q_<name>` tool. Above it, the caller gets two catalog tools instead: `search_saved_queries` (keywords → names,
descriptions and input schemas) and `run_saved_query` (`name` + `arguments`). Configure the limit in
`AddBeaconServices(..., options => options.SavedQueryTools.NamedToolLimit = 40)`.

The project brief (`get_context` with `format: "agents_md"`) lists the project's saved-query tools with their
parameters, so an agent workspace's `AGENTS.md` knows they exist.

The tools coexist with the built-in tools and with [host endpoint tools](/features/host-endpoint-tools/)
(`api_<name>`): all three are served side by side.

## Using one from a Spaces routine

A Kvika Work Space routine that refreshes a dashboard's data calls the tool and writes the structured rows as JSON —
no SQL in the routine:

```ts
// routines/refresh-loan-book.ts
import { writeFile } from "node:fs/promises";

const result = await mcp.callTool("beacon", "q_loan_book_by_month", {
  from: "2025-10-01",
  to: "2026-10-01",
});

if (result.isError) {
  throw new Error(result.content[0].text);
}

const { rows, truncated, versionNumber } = result.structuredContent;
if (truncated) {
  throw new Error("Loan book result was truncated; narrow the date range or raise the project's row limit.");
}

await writeFile(
  "dist/data/loan-book.json",
  JSON.stringify({ generatedFrom: `q_loan_book_by_month v${versionNumber}`, rows }, null, 2),
);
```

When the approved SQL changes, the routine picks up the new version on its next run without an edit — and the
`versionNumber` it records says which reviewed SQL produced the file.

## Security model

- **Authorization is the caller's projects.** A tool is visible in a project only when that project contains
  **every** data source the approved version reads. Listing and calling hard-filter on the caller's authorized
  projects (the API key's `allowed_projects` or the mapped caller's projects): a query over a data source shared by
  projects A and B shows in both only if each contains all of its sources, and a caller of A never sees a query that
  needs a B-only source. A tool outside your projects answers exactly like one that does not exist. A caller with no
  project access sees no tools (fail closed). When your credentials reach the same tool through several projects,
  the schema offers `project_id` and the call requires it.
- **Execute scope.** `/beacon/mcp` requires the Execute scope, so a Read-scoped API key cannot call saved-query
  tools; project-restricted API keys work and are held to their project list.
- **Read-only execution (§1.5).** Every step's SQL, with its arguments bound, goes through the shared execution
  gate (regex guardrail → AST read-only validator → row limit) and runs through the provider's read-only variant — a
  `READ ONLY` transaction on PostgreSQL. A final query joins the steps in in-memory SQLite behind the same gate.
- **Parameters, never interpolation (§1.10).** Each `{placeholder}` is replaced by a generated database parameter
  (`@p0`, `@p1`, …) and the value is sent as a parameter; argument text never reaches the SQL.
- **Host-managed sources.** When a step reads a [host DbContext](/features/host-dbcontext/) data source, the host's
  allow-list and column policy apply — in the gate and again in the provider — and masked columns come back masked.
- **Row limits and PII.** The final result is capped at the project's MCP row limit. An intermediate step of a
  multi-step query may return at most 50,000 rows; past that the call fails instead of joining an incomplete set.
  PII columns are masked per the project's PII detection setting before rows leave a step.
- **Audit.** Every call — success or failure, including unknown tools and invalid arguments — is written to the MCP
  audit log as `q_<name>` (a search as `search_saved_queries`), with project, data source, row count and error.
  Arguments are kept only when the project's retention settings allow content, and are never written to the
  application log. The project is resolved **before** the arguments are validated, so an invalid-argument row
  follows that project's retention setting. When no project can be established (an ambiguous or foreign
  `project_id`, an unknown tool, a search across several projects) the row is structural only — tool, input size and
  error class, never argument content — rather than falling back to the global setting.
- **No learning signals.** Saved queries are pre-approved SQL, not the caller's SQL, so they are **audit-only**:
  `McpSignalService` does not record them and they never feed pattern mining or golden examples.

## Known limits

- Parameters have no defaults or allowed-value lists in the query model, so every argument is required and free-form
  within its type.
- Only database data sources run as tools; API data source steps are rejected.
- A multi-step query whose intermediate step returns no rows cannot join (the in-memory table is not created); the
  call fails with the SQLite error.
- Column types in the structured result are inferred from the values returned; an empty result has no columns.
