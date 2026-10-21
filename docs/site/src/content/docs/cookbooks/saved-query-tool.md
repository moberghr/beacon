---
title: Publish an approved saved query as an MCP tool
description: "Turn a reviewed, parameterized Beacon query into a fixed MCP tool such as q_loan_book_by_month, so agents call approved SQL instead of writing their own."
---

| | |
|---|---|
| **You end up with** | A saved query that agents call as the tool `q_<name>`, always running the approved SQL with their arguments bound as database parameters |
| **Time** | about 20 minutes, plus the reviewer's time |
| **You need** | A query with at least one step you can edit, a second person with the **Approver** permission, a **SecurityAdmin** or an Admin who can read the query, and the [approval workflow](/security/approvals/) turned on by the host |
| **Applies to** | Beacon 5.0 · Full Beacon |

:::tip[Let your AI do it]
Most of this is done in Beacon's UI by different people (author, approver, SecurityAdmin), so ask your agent to **guide you** through it and check each result. Paste this into your agent.

```text wrap
Read https://moberghr.github.io/beacon/cookbooks/saved-query-tool.md and guide me through it step by step.
Tell me which Beacon screen to open and what to enter, then wait for me to confirm. Ask me before every decision the
recipe marks as one. Never approve a change for me, and never enable the tool yourself with an account that wrote or
approved the query. Show me the result of each "Check it works" item.
```
:::

## Before you start

| Decision | Options | Default |
|---|---|---|
| Tool name | 3 to 41 characters: `a-z`, `0-9`, `_`, starting with a letter. Agents see `q_<name>` | A short noun phrase such as `loan_book_by_month` |
| Parameters | `Number`, `DateTime` or `String`, each with a description | Describe each one for the agent: the description becomes the argument's description |
| Who enables it | A SecurityAdmin, or an Admin, who can read the query | With the approval workflow on, not the person who requested or approved the version |
| Strict mode | `ApprovalWorkflow.RequireApprovedVersionToRun` on or off | On for regulated deployments: the approval must name the exact content hash |

Every argument is required; the query model has no defaults. Only database data sources run as tools, not API sources.

## 1. Write the query with parameters

Create or open the query and write the step SQL with `{name}` placeholders:

```sql
SELECT date_trunc('month', booked_at) AS month, sum(principal) AS principal
FROM loans
WHERE booked_at >= {from}
  AND booked_at <  {to}
GROUP BY 1
ORDER BY 1
```

Declare each parameter on the step with a type and a description (`from`: DateTime, "First day to include, ISO 8601"). The reserved name `project_id` cannot be used, a parameter must have a placeholder, and the same name must have the same type in every step.

Run it with the editor's preview to check the SQL, then save.

## 2. Get a version approved

With the approval workflow on, saving SQL files a version for approval and does not change the live query.

1. The author saves. The query shows that it awaits approval.
2. A different person with the **Approver** permission opens **Approvals** (`/beacon/approvals`), reads the step diff, optionally leaves a **Reviewer comment**, and chooses **Approve**.
3. The requester cannot approve their own change, and neither can anyone else who shaped it. Admins are not exempt.

Only the query's **active** version, with an approval for exactly that version, can run as a tool. A later edit is pending again; the tool keeps running the approved version until the edit is approved. See [Which version runs](/mcp/saved-query-tools/#which-version-runs).

## 3. Expose it

Do this as a SecurityAdmin (or an Admin) who is not the requester or approver of the version. An Admin may publish any query; anyone else only a query they can read (`404` otherwise).

**In the UI.** Open the query's detail page and find the **MCP tool** card. It shows the approved version that calls will run (`v4`, for example) or why none can. Tick **Expose as MCP tool**, enter the **Tool name** (the card shows `q_<name>`), optionally a **Description** (what the tool answers, as the agent reads it; up to 1000 characters), and **Save**. The status pill changes to **Exposed**.

**Through the API**, as the same signed-in user:

```http
PUT /beacon/api/queries/42/mcp-tool
Content-Type: application/json

{ "enabled": true, "name": "loan_book_by_month", "description": "Outstanding loan book per month for a date range." }
```

The name is unique across all live queries. Enabling is refused with `400` when the name is taken or invalid, or when no approved version can run, and with `403` when you requested or approved the version while the workflow is on (unless the host allows self-approval). The query detail (`GET /beacon/api/queries/{id}`) carries `mcpToolEnabled`, `mcpToolName`, `mcpToolRunnableVersionNumber` and `mcpToolIssue`. The issue explains why a tool cannot be called: no approved version, a parameter shape that cannot be a tool, or no project that holds every data source the query reads.

Disable it the same way with `"enabled": false`. Anyone who may publish can disable.

## 4. What the agent sees

The tool appears in `tools/list` for callers whose project contains **every** data source the query reads and whose project role may run saved SQL (Viewers may).

- The name `q_loan_book_by_month`, the description, and the approved version number.
- An input schema built from the parameters: `number` for Number, an ISO 8601 `string` for DateTime, a `string` (up to 4000 characters) for String. Every parameter is required, and extra arguments are refused.
- Annotations `readOnlyHint: true`, `destructiveHint: false`.

A call returns a markdown table plus structured content: `columns`, `rows`, `rowCount`, `truncated` and the `versionNumber` that ran.

**Many tools.** Up to 25 tools visible to one caller are listed by name (`SavedQueryTools.NamedToolLimit`, set in `AddBeaconServices(..., options => options.SavedQueryTools.NamedToolLimit = 40)`). Above that, the caller gets two tools instead:

| Tool | Arguments |
|---|---|
| `search_saved_queries` | `query` (keywords; empty lists all), `limit` (default 10, max 50) |
| `run_saved_query` | `name` (required), `arguments` (matching the input schema, with `project_id` when the schema lists one) |

When a credential reaches the same tool through several projects, the schema adds `project_id` and calls must pass it.

## Check it works

- [ ] The **MCP tool** card shows **Exposed** and the version number you approved.
- [ ] From a connected client (see [Connect Claude Code, Cursor or VS Code](/cookbooks/connect-coding-agent/)), `tools/list` includes `q_loan_book_by_month` (or `search_saved_queries` above the limit).
- [ ] Ask the agent: *"Call q_loan_book_by_month with from 2026-01-01 and to 2026-07-01."* It returns rows and the `versionNumber` you approved.
- [ ] Edit the query without approving: the tool still returns the old version's numbers.
- [ ] The call is in the MCP audit as `q_loan_book_by_month`. Saved-query calls are audit-only and record no learning signals.

## If something goes wrong

| Symptom | Cause and fix |
|---|---|
| **Expose as MCP tool** is disabled, or `400` "cannot be exposed" | No approved active version, or the parameters cannot form a tool. The card names the reason. |
| `403` when enabling | You requested or approved the version the tool would run. Ask a different SecurityAdmin or Admin. |
| `403` for a non-SecurityAdmin | Publishing needs the SecurityAdmin permission or Admin. |
| `404` when enabling | You cannot read the query, or it does not exist. |
| Tool enabled but not listed | The approved version is not the active one, or no project in the caller's reach holds every data source the query reads. The card's `mcpToolIssue` says which. |
| "The tool name '…' is already used" | Names are unique across live queries. Pick another. |
| Call fails with a missing or unknown argument | Every parameter is required and nothing else is accepted; dates must be ISO 8601. |
| Strict mode will not run an old tool | The approval predates content hashes. Restore the version and approve it again. |
| `truncated: true` | More rows than the project's MCP row limit. Narrow the arguments or raise the limit in **MCP settings**. |

## Next

- [Saved query tools](/mcp/saved-query-tools/): the security model, limits and a routine example.
- [Approvals](/security/approvals/): who approves, strict mode and the API.
- [Ask your data questions with an AI agent](/cookbooks/ask-your-data/): when to prefer a saved-query tool over generated SQL.
