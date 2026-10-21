---
title: Ask your data questions with an AI agent
description: "Use Beacon's MCP tools in the order that gives correct, checked answers: orient, find, ground, validate, run, explain and rate, with prompts you can paste and instructions to give your agent."
---

| | |
|---|---|
| **You end up with** | An AI client that answers questions about your data by following Beacon's tool workflow, validates its SQL before running it, and records whether an answer was right |
| **Time** | about 15 minutes |
| **You need** | An AI client already connected to Beacon ([coding agent](/cookbooks/connect-coding-agent/) or [Claude connector](/cookbooks/claude-connector-entra/)) with Execute scope, and the Editor role in the project (a Viewer reads the catalog but cannot run SQL) |
| **Applies to** | Beacon 5.0 · Full Beacon |

:::tip[Let your AI do it]
This recipe is for an AI client that is already connected to Beacon (Claude, Claude Desktop, Cursor), not for a coding agent working in a repository. Paste this into that client's chat.

```text wrap
You are connected to Beacon's MCP tools. Follow https://moberghr.github.io/beacon/cookbooks/ask-your-data.md :
start with get_context, find tables with search, ground yourself with get_query_context, validate every SQL
statement with dry_run before query, and only use ask when I want a one-shot answer. Tell me which tool you are
calling and why. Never invent table or column names.
```
:::

## Before you start

| Decision | Options | Default |
|---|---|---|
| Who writes the SQL | Your agent (`get_query_context` → `dry_run` → `query`), or Beacon (`ask`) | The agent, when you need to see and control the SQL; `ask` for quick business questions |
| Which project | One, or several | If the credential reaches several projects, the agent passes `project_id` on every call |

## How the tools fit together

Beacon sends this workflow to clients at `initialize` as server instructions, so most clients already know it. The tools, in order:

| Step | Tool | Use it to | Key arguments |
|---|---|---|---|
| 1 | `get_context` | See the project's data sources, tables, quality and documentation status | `project_id`, `format` (`overview` or `agents_md`) |
| 2 | `search` | Find tables, columns and documents by keyword | `query`, `max_results` (default 20, max 50), `offset` |
| 2 | `get_documentation` | Read the documentation of a data source or one table | `datasource_name`, `table_name`, `schema_name`, `response_format` (`concise` or `detailed`), `document` |
| 3 | `get_query_context` | Get schemas with sample values, join paths, verified examples and glossary terms ranked for one question | `question`, `datasource_name` or `datasource_id`, `max_chars` |
| 4 | `dry_run` | Check SQL through every gate without running it | `sql`, `datasource_name` or `datasource_id` |
| 5 | `query` | Run the validated SELECT | `sql`, `datasource_name` or `datasource_id`, `max_rows` (default 100) |
| alt | `ask` | Let Beacon route, write and run the SQL | `question`, `execute` (default `true`) |
| after | `feedback` | Record that an `ask` answer was `correct` or `incorrect` | `signal_id`, `verdict`, `corrected_sql`, `note` |

Every tool also takes an optional `project_id`. Full argument tables: [MCP server](/mcp/server/#tools).

If a reviewed saved query already answers the question, an approved `q_<name>` tool is better than any hand-written SQL; see [Publish an approved saved query as an MCP tool](/cookbooks/saved-query-tool/).

## 1. Orient in a project

```text wrap title="Say to Claude"
Use Beacon's get_context to describe my project: which data sources it has, the main tables, and how well they are documented.
Do not run any queries yet.
```

`get_context` is audit-only and reads no rows. Look at the documentation status it reports: a table without documentation gives the agent less to work with.

## 2. Find where data lives

```text wrap title="Say to Claude"
Where does Beacon keep information about refunds? Use search for "refund" and "chargeback", then read the
documentation of the two most likely tables with get_documentation. Tell me which columns hold the amount and the date.
```

`search` matches keywords, and meaning too when a local embedder is configured. `get_documentation` with `table_name` returns columns, types, relationships and quality rules.

## 3. Write and validate SQL before running it

```text wrap title="Say to Claude"
I want monthly refund totals for the last six months. Call get_query_context with that question, write the SQL
yourself from what it returns, run it through dry_run, and show me the SQL and the dry_run verdict. Do not call
query until I say go.
```

`get_query_context` returns real join paths and verified examples. Sections marked **(authoritative)** are human-verified. `dry_run` runs the read-only gate, a schema column check and the database's own validation, and reports every issue at once. It never executes the statement. When the verdict is valid it returns the exact SQL that would run, with the row limit applied.

Then:

```text wrap title="Say to Claude"
Go. Run that SQL with query, and tell me how many rows came back and whether the result was truncated.
```

If the result says more rows exist, raise `max_rows` or narrow the question: results are capped at the project's maximum row limit.

## 4. Ask a one-shot business question

```text wrap title="Say to Claude"
Use Beacon's ask tool: "How many orders were placed last week, by country?" Show me the SQL it generated.
```

`ask` picks the data source, writes SQL grounded in your schema, runs it and ends its answer with a `_signal_id: N_` marker. Pass `execute: false` to get the SQL without running it. A question that spans data sources is joined in memory. Use `ask` for speed and the step-by-step flow when the SQL itself matters.

## 5. Explain a number

```text wrap title="Say to Claude"
Explain where the "active customers" figure of 12,430 came from. Show the SQL behind it, the tables it reads, and
any filter that decides who counts as active. If you cannot find it in Beacon, say so rather than guessing.
```

The agent can answer from the generated SQL of an `ask` it made, or by searching the documentation and glossary. If Beacon has no definition, a good answer says that.

## 6. Rate the answer

```text wrap title="Say to Claude"
That answer is correct. Record it with Beacon's feedback tool using the signal_id from the ask response.
```

or, when you fixed the SQL:

```text wrap title="Say to Claude"
That answer is wrong: it counted cancelled orders. Record it as incorrect with feedback, using this corrected SQL: <your SQL>.
```

An `incorrect` verdict keeps the answer out of the patterns Beacon learns. A `correct` one puts it on a list where an Admin may promote it to a verified example; feedback never promotes it by itself. Corrected SQL is stored only when it passes Beacon's checks and the project allows the feedback tool to store text. Only add a `note` when you ask for one. Details: [`feedback`](/mcp/server/#feedback).

## Write it into your agent's instructions

Put this in `CLAUDE.md`, `AGENTS.md` or your client's custom instructions so every session starts with the workflow. `get_context` with `format: "agents_md"` generates a project brief you can add below it.

```md
## Beacon data tools

- Start with `get_context`. Use `search` and `get_documentation` to find tables before writing SQL.
- Write your own SQL like this: `get_query_context` for the question, then `dry_run`, then `query`. Never call
  `query` with SQL that `dry_run` has not accepted. Do not invent table or column names.
- If an approved `q_<name>` saved-query tool answers the question, call it instead of writing SQL.
- Use `ask` only for quick one-shot questions; show the generated SQL with the answer.
- Pass `project_id` on every call when the credential reaches more than one project.
- Beacon is read-only. Do not try to write, alter or drop anything, and do not try to read other projects.
- After a user confirms an `ask` answer is right or wrong, record it with `feedback` and the `signal_id`.
  Only include a `note` when the user asks for one.
- Quote the SQL and row counts you used. If a result is truncated or masked, say so.
```

## What the agent cannot do

These are enforced by Beacon, not by the prompt. See [Safety and guardrails](/mcp/server/#safety--guardrails).

| It cannot | Why |
|---|---|
| Write, alter or drop data | Only a single read-only statement passes the gate; `dry_run` shows which layer refused it. |
| Reach another project | A project outside the credential's list answers "Access denied: you do not have access to project N." |
| See unmasked PII | Columns detected as PII are masked in results; when Beacon cannot prove where a PII column went, it masks every column. |
| Read the engine's catalog or another database | SQL naming another database, or reading `information_schema`, `pg_catalog` or similar, is refused (`cross_database`, `system_catalog`). |
| Run SQL as a Viewer | `ask`, `query`, `dry_run` and `feedback` need Editor in the project. |
| Return unlimited rows or run forever | Rows, result size, statement time and concurrent queries are capped per project. |
| Teach Beacon on its own authority | `feedback` records a verdict; only an Admin promotes a verified example. |

## Check it works

- [ ] `get_context` describes your project's data sources.
- [ ] `dry_run` on a valid SELECT reports a valid verdict and the SQL with a row limit; on `DELETE FROM x` it reports an issue and nothing runs.
- [ ] `query` returns rows, with PII columns masked when the project has detection on.
- [ ] `ask` ends with `_signal_id: N_`, and `feedback` with that number answers that the verdict was recorded.
- [ ] Each call appears in the MCP audit (see [Give an AI agent access to one project only](/cookbooks/agent-one-project/#see-what-the-agent-did)).

## If something goes wrong

| Symptom | Cause and fix |
|---|---|
| "Access denied: you may read project N but not run ad-hoc queries in it." | The caller is a Viewer. Give the owner the Editor role (and Editor membership in a restricted project). |
| "Multiple projects available (IDs: …)" | Pass `project_id` on every call, or use a key limited to one project. |
| `dry_run` says the schema gate could not run | The data source has no extracted metadata yet, so the column check could not run and the verdict is invalid. Have the data source's metadata extracted, then try again. |
| `dry_run` reports `cross_database` or `system_catalog` | The SQL names another database or reads the engine catalog. Rewrite it against the data source's own tables. |
| `ask` answers "AI features not configured" | No LLM provider is configured on this Beacon. Use `get_query_context`, `dry_run` and `query` instead. |
| Answers miss joins or misread a term | Beacon only knows what it was given. Register the join, add the glossary term, or promote a correct answer; see [Knowledge base](/mcp/knowledge-base/). |
| Result is cut off | The result says it is truncated. Raise `max_rows` (up to the project's maximum) or narrow the question. |

## Next

- [Prompt library](/cookbooks/prompts/): more prompts for specific jobs.
- [Publish an approved saved query as an MCP tool](/cookbooks/saved-query-tool/): fixed, reviewed SQL for recurring questions.
- [Ground Beacon with your docs](/cookbooks/ground-with-your-docs/): better answers from better documentation.
- [MCP server](/mcp/server/): every tool and setting.
