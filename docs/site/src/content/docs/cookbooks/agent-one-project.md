---
title: Give an AI agent access to one project only
description: "Confine an agent to a single restricted project with a project-limited Execute key, set that project's masking and row limits, verify the agent cannot reach anything else, and read what it did in the MCP audit."
---

| | |
|---|---|
| **You end up with** | An API key that reaches one project and nothing else, masked and row-capped results in that project, and an audit trail of every call the agent made |
| **Time** | about 20 minutes |
| **You need** | An Admin or UserAdmin (to restrict the project and add members), a project with its data sources linked, a Beacon user for the agent's key to act as, and an Auditor or Admin to read the audit |
| **Applies to** | Beacon 5.0 · Full Beacon |

:::tip[Let your AI do it]
Most of this is done in Beacon's UI under different roles, so ask your agent to **guide you** and to check each result. Paste this into your agent.

```text wrap
Read https://moberghr.github.io/beacon/cookbooks/agent-one-project.md and guide me through it step by step.
Tell me which Beacon screen to open and what to enter, then wait for me to confirm. Ask me before every decision the
recipe marks as one. Never ask me to paste the API key into the chat. Show me the result of each "Check it works" item.
```
:::

## Before you start

| Decision | Options | Default |
|---|---|---|
| Whose identity the key uses | A dedicated Beacon user for the agent, or your own | A dedicated user: the key can never reach more than its owner does, so a small owner means a small key |
| The owner's role | Editor (the agent may run SQL) or Viewer (catalog and saved-query tools only) | Editor in this project, Viewer elsewhere |
| Masking | PII detection on, plus custom patterns for your own identifiers | On; it is pinned on by `Beacon:Mcp:ForcePiiDetection` unless your host sets it to `false` |
| Row cap | A project maximum below the global 1000 | The lowest value the agent needs |

## 1. Use a restricted project

A restricted project exists only for its members and Admins; to anyone else it answers `404`. New projects are created restricted, with the creator as project admin.

1. Create the project (**Projects → New project**), or open an existing one.
2. On the project page, the **Access** card (UserAdmin or Admin) shows **Open** or **Restricted**. Choose **Restricted** and confirm. Add members before restricting an existing project, or its users lose access.
3. **Link data sources** with only the sources the agent needs. A resource reading several data sources is reachable only through a project that holds all of them.
4. On the **Members** tab, add the agent's Beacon user with the **Editor** role (or **Viewer** to limit it to the catalog and saved-query tools).

:::caution
Project access decides who may run SQL through Beacon, not what the database login can read. Give each data source a login that sees only what this project should see. Beacon also treats a data source that another open project links as reachable through that project; check the **Access** card's list of projects that share a source.
:::

## 2. Create a key limited to that project

Sign in as the agent's user (keys are created in a signed-in browser session, never with another key), open **API Keys** (`/beacon/api-keys`) and choose **Generate key**:

- **Key name**: for example `Reporting agent`.
- **Scopes**: **Execute**. The MCP endpoint requires it; it needs the Editor role or above on the owner.
- **Projects**: only this project. The picker lists projects the owner can read.
- **Expiration date**: a short one. The default is 90 days.

Copy the key (`sk-sem_…`) once, and store it in your secret store or an environment variable; see [Connect Claude Code, Cursor or VS Code](/cookbooks/connect-coding-agent/) for wiring it into a client.

What the key can reach is the **intersection** of its project list and what its owner may still use, at no more than the owner's role there, and never the Admin bypass. Remove the owner's membership, and the key reaches nothing. See [Project restrictions](/security/api-keys/#project-restrictions).

## 3. Set the project's masking and limits

Open **MCP settings** (`/beacon/mcp-settings`), switch the scope selector at the top from the global defaults to your project, and tick **Override** on each field you want to set for this project only:

| Field (tab) | Set it to | Effect |
|---|---|---|
| **Max row limit** (Guardrails) | e.g. 200 | Ceiling on rows `query`, `ask` and saved-query tools return in this project. Must be at least 1. |
| **Enable PII detection** and **Custom PII patterns** (Guardrails) | On, plus one regex per line | Masks detected PII columns in results. Add patterns for identifiers Beacon cannot detect. |
| **Enforce read-only queries** (Guardrails) | On | Keep on; with `ForceReadOnly` (default) it cannot be turned off. |
| **Statement timeout (s)**, **Max result size (bytes)** (Execution) | e.g. 15 and 65536 | Bounds each statement and its payload. |
| **Max concurrent queries per API key** (Execution) | e.g. 2 | Limits parallel calls from this key. |
| **Retain question and SQL text in signals and audit** (Execution) | Your policy | Whether the audit keeps what the agent asked and ran. Off keeps the audit structural only. |

Editing needs the SecurityAdmin permission (an Auditor can read it). Deployment locks and ceilings in `Beacon:Mcp` can pin or cap these values from configuration; a locked field is hidden or disabled and says so. When a data source is shared with other projects, the strictest settings among them apply. See [Safety and guardrails](/mcp/server/#safety--guardrails).

## 4. See what the agent can and cannot reach

Connect a client with the key and try, in this order:

| Ask the agent | What happens |
|---|---|
| "Run `get_context`." | Describes this project. With a single-project key no `project_id` is needed. |
| "Run `get_context` with `project_id` 99" (another project) | "Access denied: you do not have access to project 99." The call is audited. |
| "Query the customers table." | Rows return with PII columns masked, capped at the row limit; the result says when it was truncated. |
| "Run `DELETE FROM customers`" | Refused by the read-only gate. `dry_run` shows which layer refused it. |
| "Query `information_schema.tables`" or another database | Refused (`system_catalog`, `cross_database`). |

The key cannot change settings, manage keys, members or data sources: API keys never reach administrative routes, and they hold no functional permission.

## See what the agent did

Every tool call, including refusals and errors, is written to the MCP audit with the key (`ApiKeyId`), its owner, the tool, the project, timing, row counts and the statements it ran (`sql` only when the project retains content). An Auditor or an Admin reads it:

```http
GET /beacon/api/mcp/audit?from=2026-10-01T00:00:00Z&to=2026-10-08T00:00:00Z&projectId=4&tool=query&page=1&pageSize=100
```

`from` and `to` are required (UTC, at most 93 days apart); `projectId`, `tool`, `callerHash` and `userId` filter. The rows are not filtered by project membership, so give the role only to people who should see every project's calls. An Auditor or Admin can also read it on the **MCP Audit** page (`/beacon/mcp-audit`), which calls this endpoint. Reading the audit is itself recorded in the [security audit trail](/security/audit-trail/), where creating and revoking keys is also logged. Details: [Reading the audit](/mcp/observability-and-audit/#reading-the-audit).

To stop the agent at once, **Revoke** the key on **API Keys**. It takes effect on the next request. Disabling the owner revokes all of their keys.

## Check it works

- [ ] The project page shows **Restricted**, and a user who is not a member gets "not found" for it.
- [ ] The key lists exactly one project on **API Keys**.
- [ ] A call naming another project is refused with "Access denied: you do not have access to project N."
- [ ] A result from `query` has PII columns masked and no more rows than **Max row limit**.
- [ ] A write statement is refused.
- [ ] The audit export shows the calls above, including the refused ones, for this key.

## If something goes wrong

| Symptom | Cause and fix |
|---|---|
| "No projects are available to you." | The key's project is not one its owner may still use: the owner is not a member of the restricted project, or was disabled. Add the membership. |
| "Access denied: you may read project N but not run ad-hoc queries in it." | The owner is a Viewer there. Make them an Editor member for `ask`, `query` and `dry_run`. |
| "Permission denied: this credential does not have the Execute scope" | The key is `Read`, or its owner lost the Editor role. |
| Cannot tick **Execute** when creating the key | The signed-in user is a Viewer. Execute keys need the Editor role or above. |
| The **Access** card is missing | It is shown to UserAdmins and Admins only, and is hidden when the host manages membership. |
| Restricting answers `409` | Project membership is unavailable in this deployment (the host manages it, or user management is off). |
| A settings field cannot be changed | A deployment lock pins it, or a ceiling caps it; only the host's configuration lifts that. |
| The key stopped working with a message about owner inactivity | The host sets `Beacon:ApiKeys:MaxOwnerInactivityDays` and the owner has not signed in for that long. A service account must sign in now and then. |
| Audit export answers `403` | The caller is neither an Auditor nor an Admin. |

## Next

- [Authorization](/security/authorization/): project roles, 404 versus 403, shared data sources.
- [API keys](/security/api-keys/): scopes, expiry, owner changes.
- [MCP observability and audit](/mcp/observability-and-audit/): audit fields, retention, tracing.
- [Audit to your SIEM](/cookbooks/audit-to-siem/): ship these events elsewhere.
