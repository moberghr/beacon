---
title: Knowledge Base & SQL Grounding
description: How Beacon grounds generated SQL — M-Schema with sample values, schema relationships, a business glossary, human-verified golden examples, learned patterns, and semantic retrieval.
---

Beacon does not hand a bare schema dump to an LLM and hope. Every natural-language question — through the [`ask` tool](/features/mcp-server/#ask), the [MCP Playground](/features/mcp-server/#mcp-playground), or the [`get_query_context` tool](/features/mcp-server/#get_query_context) — is answered against an assembled **grounding context**: the tables that matter, the join paths that are real, the business terms your organisation actually uses, and query examples a human has verified.

This page describes what goes into that context, where each piece comes from, and how you curate it.

## The grounding context

For a given question and data source, Beacon assembles the context in blocks. Sections marked **(authoritative)** are human-verified; everything else is machine-derived and labelled as such.

| Block | Contents | Source |
|---|---|---|
| **Relevant Tables (full schema)** | M-Schema rendering: column name, type, nullability, description, plus **real sample values** per column | Schema metadata + sample-value collection |
| **Other Tables** | Names and primary keys only, so the model knows what else exists without burning the budget | Schema metadata |
| **Join Paths (verified)** | Foreign keys and human-confirmed relationships | [Schema relationships](#schema-relationships) |
| **Join Paths (UNVERIFIED)** | Relationships inferred from column naming, explicitly flagged as needing confirmation | Relationship discovery |
| **Coverage** | A note when the table neighbourhood was capped, so the model knows the picture is partial | Context assembly |
| **Business Glossary** | Top-K matching terms with definitions and their column/metric mapping | [Business glossary](#business-glossary) |
| **Verified query examples (authoritative)** | Human-verified question → SQL pairs for this data source | [Golden examples](#golden-examples-and-the-eval-harness) |
| **Learned Patterns (from usage)** | Known corrections, column clarifications, join patterns, common queries, business-term mappings, documentation gaps | [The learning loop](#the-learning-loop) |

Sample values are what make filters land on real data (`'shipped'` vs `'SHIPPED'`). They are collected per column and can be switched off with the `EnableSampleValueCollection` setting.

:::tip
Everything on this page is reachable from the outside: `get_query_context` returns the same assembled context an `ask` call would use, so an agent can write its own SQL against the same grounding, validate it with `dry_run`, and execute it with `query`.
:::

## Schema relationships

Join paths are the single biggest source of wrong SQL, so Beacon keeps them as first-class, curatable data rather than guessing at generation time.

Each registered relationship maps a source column to a target column and carries an **origin** and a **cardinality**:

| Origin | Meaning | Verified by default |
|---|---|---|
| **Foreign key** | Read from the database's declared constraints | Yes |
| **Inferred** | Proposed from column-naming heuristics (`customer_id` → `customers.id`) with a confidence score | No |
| **Manual** | Declared by a person in the UI or via the API | Yes |

Cardinality is one of `Unknown`, `One to one`, `One to many`, `Many to many`.

Only **verified** relationships are rendered under *Join Paths (verified)*. Unverified inferences appear in a separate block that tells the model to confirm before relying on them — a wrong join path that looks authoritative is worse than no join path at all.

### Managing relationships in the UI

Open a data source and go to **Schema relationships** (`/data-sources/{id}/relationships`):

- **Discover** previews inferred relationships without saving anything — review each proposal with its confidence score and **Accept** the ones that are right.
- **Add relationship** declares one by hand; declared relationships are treated as verified.
- Existing entries can be relabelled, re-verified, or deleted.

The page opens with a **schema health** panel: table count, relationship count, how many are still unverified, the number of connected groups, the largest connected group, isolated tables, and detected junction tables. Isolated tables and a high group count are the signal that the model has no way to join its way to those tables.

### REST API

The relationship endpoints sit under the standard authenticated `BeaconApi` policy — grounding metadata every user's questions depend on, not an admin-only surface.

| Method | Endpoint |
|---|---|
| `GET` | `/beacon/api/data-sources/{dataSourceId}/relationships` (filter by `origin`, `verifiedOnly`) |
| `POST` | `/beacon/api/data-sources/{dataSourceId}/relationships` |
| `PUT` | `/beacon/api/data-sources/{dataSourceId}/relationships/{relationshipId}` |
| `POST` | `/beacon/api/data-sources/{dataSourceId}/relationships/{relationshipId}/verify` |
| `DELETE` | `/beacon/api/data-sources/{dataSourceId}/relationships/{relationshipId}` |
| `POST` | `/beacon/api/data-sources/{dataSourceId}/relationships/discover-preview` |
| `GET` | `/beacon/api/data-sources/{dataSourceId}/schema-health` |

## Business glossary

The glossary teaches Beacon your organisation's vocabulary: what "active customer", "churn", or "net revenue" means in *your* schema. Terms are project-scoped and optionally narrowed to a single data source.

| Field | Purpose |
|---|---|
| `Term` | The business term as people say it |
| `Synonyms` | Alternative phrasings that should match the same term |
| `Definition` | Plain-language definition shown to the model |
| `TargetSchema` / `TargetTable` / `TargetColumn` | Where the term lives in the schema |
| `MetricExpression` | The SQL expression that computes it, when the term is a metric |
| `IsActive` | Retire a term without deleting its history |

When a question is asked, the top matching terms (`GlossaryTopK`, default 5) are injected into the **Business Glossary** block. Glossary retrieval is scoped to the caller's authorised project — two projects sharing a data source never see each other's definitions.

The glossary is an **API-managed governance surface** and is gated with the Admin policy; there is no dedicated UI page for it yet.

| Method | Endpoint |
|---|---|
| `GET` | `/beacon/api/glossary?projectId={id}&dataSourceId={id}&includeInactive={bool}` |
| `POST` | `/beacon/api/glossary` |
| `PUT` | `/beacon/api/glossary/{id}` |
| `DELETE` | `/beacon/api/glossary/{id}` |

## Golden examples and the eval harness

A **golden case** is a human-verified question → SQL pair. Golden cases do double duty: they are injected into the grounding context as *authoritative* examples, and they are the regression suite the eval harness scores generation against.

### How cases are created

- **From feedback.** Rating an `ask` answer `correct` — through the `feedback` MCP tool, the MCP Playground, or `POST /beacon/api/eval/feedback` — auto-promotes that signal into a golden case, once per signal. Any corrected SQL you supply is what gets stored.
- **By promotion.** `POST /beacon/api/eval/golden/promote` promotes a specific signal by id with a note.
- **By hand.** `PUT /beacon/api/eval/golden/{id}` edits the question, the gold SQL, the notes, or deactivates the case.

### Injection

The top `GoldenExemplarTopK` (default 4) cases matching the question are rendered into **Verified query examples (authoritative)**, ranked *above* mined learned patterns and capped by `GoldenExemplarBudgetChars` (default 2000). Retrieval hard-filters on the caller's project, so golden SQL never leaks across project boundaries on a shared data source.

### Eval runs

`POST /beacon/api/eval/runs` replays every active golden case for a project through the real generation pipeline and scores the result. Comparison is by **result-set fingerprint** — the generated SQL doesn't have to match the gold text, it has to produce the same rows. Each failure is tagged so you can tell a retrieval failure (the model never saw the right table) from a SQL-reasoning failure from an execution failure.

With `EnableEvalJudge` turned on, an opt-in LLM judge reviews a capped, PII-redacted sample of the rows to distinguish a cosmetically different result from a wrong one. It is off by default.

Both the gold SQL and the generated SQL are re-validated read-only and run through the query guardrail before execution — the harness inherits nothing and introduces no new execution path.

| Method | Endpoint |
|---|---|
| `POST` | `/beacon/api/eval/runs` |
| `GET` | `/beacon/api/eval/runs?projectId={id}&take={n}` |
| `GET` | `/beacon/api/eval/runs/{runId}/results` |
| `GET` | `/beacon/api/eval/golden?projectId={id}&dataSourceId={id}` |
| `PUT` | `/beacon/api/eval/golden/{id}` |
| `POST` | `/beacon/api/eval/golden/promote` |
| `POST` | `/beacon/api/eval/feedback` |

The eval group is Admin-gated: it executes SQL against live data sources and writes golden cases.

## The learning loop

Every `ask`, `query`, and `dry_run` call records a **usage signal** — the question, the generated SQL, the tables and columns referenced, the routing decision, the outcome, and timings. A recurring job turns those signals into reusable knowledge.

![MCP Learning](/img/screenshots/mcp-learning-dark.png)

### From signal to injected lesson

1. **Mining.** Signals are clustered per project and data source. Only `ask` and `query` signals are mined — `dry_run` rows carry SQL rather than a natural-language question and are excluded so they cannot skew NL pattern mining or documentation-gap error rates. They remain in the signals table for analytics.
2. **Lesson extraction.** For each failure-then-correction cluster, an LLM is asked to diagnose the failure and emit one compact, durable lesson. Only the cluster's text fields are sent — never result rows. If the provider is unavailable or the response doesn't parse, a deterministic regex + column-similarity path produces the lesson instead, so the loop never silently stops.
3. **Classification.** Lessons become learned patterns of type `SchemaCorrection`, `ColumnClarification`, `JoinPattern`, `CommonQuery`, `BusinessTermMapping`, or `DocumentationGap`.
4. **Gating.** A pattern whose confidence clears `LearningAutoApproveThreshold` (default 0.7) is auto-approved. Everything else lands as `Pending` or `NeedsEvidence` for human review on the **MCP Learning** page (`/mcp-learning`), where you approve or reject each one.
5. **Replay verification.** With `EnableReplayVerification` on (the default), a `NeedsEvidence` candidate is not promoted on a confidence number — it is *replayed*. Beacon re-runs the project's relevant golden cases with and without the candidate injected and promotes it only if it flips at least `LearningReplayMinFlips` (default 1) case from fail to pass. Replay is scoped to the candidate's own project and data source.
6. **Injection.** Approved and auto-approved patterns are ranked and injected into **Learned Patterns (from usage)** under `LearningInjectionBudgetChars` (default 1500), filtered to the caller's authorised project.

### Decay

A schema correction points at a specific column. When a schema refresh shows that column no longer exists, the correction is marked superseded and is never injected again. The row is kept for audit rather than deleted.

### Retention

Signals older than `LearningSignalRetentionDays` (default 90) are removed by the nightly cleanup job.

## Semantic retrieval

Keyword matching alone misses the question that says "revenue" when the column is called `net_amount`. Beacon adds a dense retrieval arm and fuses the two.

### Local embeddings

Embeddings are produced **in-process by a local ONNX model** — a 384-dimension bge-small-en-v1.5 with a WordPiece tokenizer, 512-token inputs. There is no network egress and no third-party embedding API. The model and tokenizer are local files you supply; embeddings are **disabled by default** so an install without a model keeps working on the lexical path.

```json
{
  "Beacon": {
    "Embeddings": {
      "Enabled": true,
      "ModelPath": "/opt/beacon/models/bge-small-en-v1.5.onnx",
      "TokenizerPath": "/opt/beacon/models/vocab.txt"
    }
  }
}
```

On PostgreSQL, vectors are additionally written to a **pgvector** column with an HNSW cosine index so nearest-neighbour search runs in the database — this requires the pgvector extension to be available on the server, since the migration that adds the column runs `CREATE EXTENSION IF NOT EXISTS vector`. On SQL Server the same vectors are compared in memory from their byte representation, with no extension required. Semantic retrieval works on both; PostgreSQL is just faster at it.

### Fusion

Where a dense arm is available, its ranked results are merged with the keyword results using **reciprocal rank fusion**, with deterministic tie-breaks so paging stays stable. This drives:

- The `search` MCP tool across tables, columns, and documentation.
- Exemplar selection for golden examples and learned patterns (`ExemplarTopK`, default 4).
- Documentation chunk retrieval (`DocChunkTopK`, default 6).

Every dense path **fails open**: with no embedder configured, or with rows not yet indexed, retrieval falls back to the keyword/overlap path and returns results rather than failing.

### Documentation chunking

Generated project documentation is split into overlapping sentence windows (`DocChunkWindowSentences`, default 5; `DocChunkOverlapSentences`, default 1) and embedded so a question can retrieve the paragraph that answers it rather than a whole document. With `EnableContextualRetrieval` enabled, each chunk is additionally given a short LLM-written blurb situating it in its parent document before embedding — more accurate retrieval, at the cost of one LLM call per chunk at index time.

Fusion for documentation is at **section** granularity: two chunks of the same section collapse into one result.

## Background jobs

Indexing and aggregation run as recurring [Warp](https://moberghr.github.io/warp/) jobs in the host, visible on the `/warp` dashboard:

| Job | Schedule | What it does |
|---|---|---|
| `mcp-learning-aggregate` | Every 6 hours | Mines signals into learned patterns, runs decay and replay verification |
| `mcp-learning-cleanup` | Daily at 03:00 | Removes signals past the retention window |
| `mcp-embedding-reindex` | Every 12 hours | Re-embeds schema metadata and exemplars |
| `mcp-docchunk-reindex` | Every 12 hours | Re-chunks and re-embeds project documentation |

## Settings reference

These live in `McpSettingsData` and round-trip through `GET`/`PUT /beacon/api/mcp/settings`. The **MCP Settings** page (`/mcp-settings`) exposes the prompt, tool-description, and guardrail/learning knobs — the max row limit, read-only enforcement, PII patterns, and the four `Learning*` settings below. Everything else in this table is currently API-only.

| Setting | Default | Effect |
|---|---|---|
| `EnableSampleValueCollection` | `true` | Collect real sample values for the M-Schema block |
| `EnableLearning` | `true` | Master switch for signal mining |
| `LearningAutoApproveThreshold` | `0.7` | Confidence at or above which a pattern auto-approves |
| `LearningInjectionBudgetChars` | `1500` | Character budget for the learned-patterns block |
| `LearningSignalRetentionDays` | `90` | Signal retention window |
| `EnableReplayVerification` | `true` | Gate `NeedsEvidence` promotions on golden-case replay |
| `LearningReplayMinFlips` | `1` | Fail→pass flips a candidate must produce to be promoted |
| `EnableSemanticRetrieval` | `true` | Use the dense arm when an embedder is available |
| `ExemplarTopK` | `4` | Exemplars selected per question |
| `EnableGoldenExemplars` | `true` | Inject human-verified examples |
| `GoldenExemplarTopK` | `4` | Golden cases injected |
| `GoldenExemplarBudgetChars` | `2000` | Character budget for the golden-examples block |
| `GlossaryTopK` | `5` | Glossary terms injected |
| `EnableContextualRetrieval` | `false` | LLM-written situating blurb per doc chunk at index time |
| `DocChunkWindowSentences` | `5` | Sentences per documentation chunk |
| `DocChunkOverlapSentences` | `1` | Sentence overlap between chunks |
| `DocChunkTopK` | `6` | Documentation chunks retrieved |
| `EnableSelfConsistency` | `false` | Generate several SQL candidates and vote |
| `SelfConsistencyCandidateCount` | `5` | Candidates generated when voting is on |
| `EnableEvalJudge` | `false` | LLM judge adjudicates near-miss eval results |

## Project isolation

Grounding is retrieval, and retrieval is not authorisation — so every retrieval path is explicitly scoped to the project the caller is authorised for, not to the data source. On a data source shared by several projects, the glossary terms, golden examples, learned patterns, and replay cases one project sees are only ever its own.

## See Also

- [MCP Server](/features/mcp-server/) — the tools that consume this context, and the guardrails around them
- [AI Integration](/features/ai-integration/) — documentation generation and the LLM provider abstraction
- [Data Sources](/features/data-sources/) — schema metadata loading
- [Admin Settings](/features/admin-settings/) — LLM provider configuration
