---
title: Saved Query Tools
description: Expose approved, parameterized saved queries as named, read-only MCP tools such as q_loan_book_by_month(from, to), so agents and routines call reviewed, versioned SQL instead of carrying their own.
---

An agent that writes its own SQL for a recurring question gets a slightly different query every time. A **saved query
tool** turns a reviewed Beacon query into a fixed MCP tool, such as `q_loan_book_by_month(from, to)`, that always runs the
same approved SQL with the caller's arguments bound as database parameters. Routines become deterministic and
cheaper: they call a tool instead of shipping `queries/*.sql` files or asking `ask` to regenerate SQL.

## Expose a query

1. Write the query as usual. Parameters are `{name}` placeholders in the step SQL, with a type (Number, DateTime,
   String) and a description. The description becomes the argument's description in the tool schema.
2. Get a version **approved** (see [the approval rule](#which-version-runs)). With the approval workflow enabled,
   saving SQL changes submits a version for approval; an Approver (or an Admin) other than the requester approves it on
   **Approvals** (`/beacon/approvals`; see [Queries → Approval workflow](/features/queries/#approval-workflow)).
3. On the query's detail page, a **SecurityAdmin** (or an Admin) opens the **MCP tool** card, ticks *Expose as MCP
   tool*, sets a name and (optionally) a description, and saves. The card shows which approved version calls run, or why
   none can. While the approval workflow is on, publishing is a third pair of eyes: whoever requested or approved the
   version the tool runs cannot enable the tool, nor can a caller Beacon cannot name (an API key without an active
   owner), who could be either (`403`), unless the host allows self-approval (`ApprovalWorkflow.AllowSelfApproval`).
   The check is made when the tool is enabled: once published, the tool serves whichever version is approved next,
   whoever requested or approved that one. With the workflow off nobody approves a change, and enabling is not checked
   against approvals.

The same through the API (the SecurityAdmin policy, see [Functional permissions](/security/authorization/#functional-permissions); anyone but an Admin publishes only a query they can read, `404` otherwise):

```http
PUT /beacon/api/queries/42/mcp-tool
Content-Type: application/json

{ "enabled": true, "name": "loan_book_by_month", "description": "Outstanding loan book per month for a date range." }
```

Rules the endpoint enforces:

| Field | Rule |
| --- | --- |
| `name` | `^[a-z][a-z0-9_]{2,40}$`; the MCP tool is `q_<name>`. Unique across live (non-archived) queries, so it is unique in every project and always names one query. Required to enable. Two concurrent saves of the same name get the same "already used" error (`400`), never a `500`. |
| `description` | Optional, at most 1000 characters. Falls back to the approved version's description, then its name. |
| `enabled: true` | Requires a runnable approved version whose parameters make a valid tool (see below), and, while the approval workflow is on, a signed-in caller with a user id who neither requested nor approved that version (`403` otherwise, unless the host allows self-approval; checked when enabling, not again when a later version is approved). Disabling is always allowed, by anyone who may publish; the name stays reserved while it is set. Disabling with a name another query already uses succeeds and keeps the query's stored name. |

The query detail DTO (`GET /beacon/api/queries/{id}`) carries `mcpToolEnabled`, `mcpToolName`,
`mcpToolDescription`, `mcpToolRunnableVersionNumber` and `mcpToolIssue`. `mcpToolIssue` explains why the query is not
a tool anyone can call: no approved active version, a parameter shape that cannot be a tool, or **not visible in any
project**: no single project contains all of the data sources its steps read, so no caller can ever see it. The
last one does not block enabling (a data source may be added to a project later); it is reported so the tool does
not silently stay invisible.

## Which version runs

A tool runs **only** the query's active version, and only when an approval request for **exactly that version** was
approved. In the workflow's strict mode (`ApprovalWorkflow.RequireApprovedVersionToRun`) the approval must also have
recorded the version's content hash, which binds it to exactly the content the reviewer approved: an approval recorded
before Beacon kept content hashes names the version but not what it approved, so strict mode does not run that tool until
the version is approved again (restore it and approve the submission). Outside strict mode such an
approval still counts, and with the workflow on the host logs a startup Warning naming those tools (at most 20, with
their count). In strict mode the startup Warning counts instead the queries with a tool or a subscription whose active
version is not approved for its content hash. Nothing else is callable:

| Version state | Callable? |
| --- | --- |
| Active, its approval request Approved (in strict mode: for the version's content hash) | **Yes**. This is the only runnable version |
| Active, approved before Beacon kept content hashes | Outside strict mode only (named in the startup Warning) |
| Pending approval (a later edit) | No. The tool keeps running the approved version until the edit is approved |
| Rejected, or superseded by a newer submission | No |
| Active without an approval (edited while the approval workflow was off, or restored from history while it was off) | No |
| Draft / no version | No |

A query with no runnable version is **not listed** as a tool at all, even when enabled. The tool executes the
approved version's **snapshot** (its stored steps and final query), not the live step rows, so an unreviewed change
to the live query can never change what the tool runs. In practice: enable the approval workflow on hosts that use
saved-query tools. It is off by default; turn it on in code with `options.EnableApprovalWorkflow(...)` in
`AddBeaconServices` (see [Approvals](/security/approvals/)). With the workflow on, a version restore is
submitted for approval like an edit, so restoring an older version never makes it callable without a new approval.
Subscriptions follow the same rule in the workflow's strict mode
(`ApprovalWorkflow.RequireApprovedVersionToRun`, see [Queries → Approval workflow](/features/queries/#approval-workflow)).

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
`q_<name>` tool. Above it, the caller gets two catalog tools instead:

| Tool | Parameters | Returns |
| --- | --- | --- |
| `search_saved_queries` | `query` (string, keywords matched against names and descriptions; empty lists every tool), `limit` (integer, default 10, max 50) | names, descriptions and input schemas |
| `run_saved_query` | `name` (string, required), `arguments` (object matching the query's input schema, including `project_id` when the schema lists one) | the same result as the named tool |

Configure the limit in `AddBeaconServices(..., options => options.SavedQueryTools.NamedToolLimit = 40)`.

The project brief (`get_context` with `format: "agents_md"`) lists the project's saved-query tools with their
parameters, so an agent workspace's `AGENTS.md` knows they exist.

The tools coexist with the built-in tools and with [host endpoint tools](/mcp/host-endpoint-tools/)
(`api_<name>`): all three are served side by side.

## Using one from a routine

An agent routine that refreshes a dashboard's data calls the tool and writes the structured rows as JSON, with no SQL
in the routine. The `mcp.callTool` call below stands for whatever MCP client the routine runs in:

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

When the approved SQL changes, the routine picks up the new version on its next run without an edit, and the
`versionNumber` it records says which reviewed SQL produced the file.

## Security model

- **Authorization is the caller's projects.** A tool is visible in a project only when that project contains
  **every** data source the approved version reads, and only in the caller's projects where its
  [project access](/security/authorization/#project-access) lets it run saved SQL (`Run`; the projects an API key or
  mapped caller reaches are described in the [MCP Server guide](/mcp/server/#overview)): a query over a
  data source shared by projects A and B shows in both only if each contains all of its sources, and a caller of A
  never sees a query that needs a B-only source. A tool outside your projects answers exactly like one that does not
  exist. A caller with no project access sees no tools (fail closed). When your credentials reach the same tool
  through several projects, the schema offers `project_id` and the call requires it.
- **Re-checked before it runs.** Right before any SQL runs, the call checks the steps it is about to run again: the
  caller may still run saved SQL in the project, and the project still links every data source those steps read.
  Otherwise the call fails ("not available in project N for your credentials") without running anything, and is
  audited.
- **Execute scope.** `/beacon/mcp` requires the Execute scope, so a Read-scoped API key cannot call saved-query
  tools; project-restricted API keys work and are held to their project list.
- **Read-only execution.** Every step's SQL, with its arguments bound, goes through the shared execution gate
  (regex guardrail, AST read-only validator, cross-database rule, system catalog rule, row limit) and runs through the provider's read-only
  path: a `READ ONLY` transaction on PostgreSQL and MySQL; on the other engines the parser gates and the read-only
  login you connect with (see [How a query runs](/features/queries/#how-a-query-runs)). A final query joins the steps
  in in-memory SQLite behind the same gate.
- **Parameters, never interpolation.** Each `{placeholder}` is replaced by a generated database parameter
  (`@p0`, `@p1`, …) and the value is sent as a parameter; argument text never reaches the SQL.
- **Host-managed sources.** When a step reads a [host DbContext](/mcp/host-dbcontext/) data source, the host's
  allow-list and column policy apply, in the gate and again in the provider, and masked columns come back masked.
- **Row limits and PII.** The final result is capped at the project's MCP row limit. An intermediate step of a
  multi-step query may return at most 50,000 rows; past that the call fails instead of joining an incomplete set.
  PII columns are masked per the project's PII detection setting before rows leave a step.
- **Audit.** Every call, whether it succeeds or fails and including unknown tools and invalid arguments, is written to the MCP
  audit log as `q_<name>` (a search as `search_saved_queries`), with project, data source, row count and error.
  Arguments are kept only when the project's retention settings allow content, and are never written to the
  application log. The project is resolved **before** the arguments are validated, so an invalid-argument row
  follows that project's retention setting. When no project can be established (an ambiguous or foreign
  `project_id`, an unknown tool, a search across several projects) the row is structural only (tool, input size and
  error class, never argument content) rather than falling back to the global setting.
- **No learning signals.** Saved queries are pre-approved SQL, not the caller's SQL, so they are **audit-only**:
  `McpSignalService` does not record them and they never feed pattern mining or golden examples.

## Known limits

- Parameters have no defaults or allowed-value lists in the query model, so every argument is required and free-form
  within its type.
- Only database data sources run as tools; API data source steps are rejected.
- A multi-step query whose intermediate step returns no rows cannot join (the in-memory table is not created); the
  call fails with the SQLite error.
- Column types in the structured result are inferred from the values returned; an empty result has no columns.
