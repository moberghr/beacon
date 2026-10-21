---
title: Ground AI answers in your own docs and data model
description: "Ship your Markdown documentation and your EF Core model with your application so AI agents find them through search, get_documentation and get_context, and check that an agent actually finds them."
---

| | |
|---|---|
| **You end up with** | Your documents imported into a Beacon project and, in full Beacon, your `DbContext` exposed as a read-only data source, plus a check that an agent finds both |
| **Time** | about 25 minutes |
| **You need** | An application with Beacon wired in ([MCP gateway](/mcp/gateway/) or [Integrate Beacon](/integrate/)), Markdown files in your repository, and for the data model part full Beacon with an EF Core `DbContext` and a read-only database login |
| **Applies to** | Beacon 5.0 · Both. Documents: gateway and full Beacon. `ExposeDbContext`, the glossary and golden examples: full Beacon only, because the gateway has no data sources and no SQL tools |

:::tip[Let your AI do it]
Paste this into Claude Code (or another coding agent) in your application's repository.

```text wrap
Read https://moberghr.github.io/beacon/cookbooks/ground-with-your-docs.md and follow it step by step in this repository.
Ask me whether this is the MCP gateway or full Beacon, which folder holds the docs, and which tables may be exposed.
Never expose a table I did not name, never put a connection string in code, and show me the result of each
"Check it works" item.
```
:::

## Before you start

| Decision | Options | Default |
|---|---|---|
| Where the documents live | A folder copied to the output, or embedded in the assembly | Embedded, so the deployment cannot lose them |
| Expose the data model | Yes (full Beacon only), or documents only | Documents only |
| Tables an agent may read | An explicit allow-list | None: `ExposeDbContext` is default-deny |
| Columns hidden or masked | Excluded (invisible) or masked (visible, values `***`) | Mask national ids and account numbers, exclude secrets |

## 1. Write documents that ground well

Each document is split into overlapping sentence windows (5 sentences, 1 overlap, by default), so a paragraph is
retrieved on its own. Write for that.

- [ ] One topic per page, with a `title:` in the frontmatter (else the first `# ` heading is used).
- [ ] Each paragraph makes sense without the paragraphs around it: name the thing in full instead of "it".
- [ ] Use the words your people use in questions, and say once what they mean: "active customer means ...".
- [ ] Put definitions, rules and "where to find it" first, narrative last.
- [ ] State units, time zones and currencies.
- [ ] Keep each file under 512 KB (the default `MaxDocumentBytes`; larger files are skipped with a warning).
- [ ] No secrets, no personal data, no real customer names. The documents are readable by every caller who can read
      the project.

Only `.md`, `.markdown` and `.txt` files are imported.

## 2. Ship the documents

Embed them in the assembly:

```xml
<ItemGroup>
  <EmbeddedResource Include="..\docs\wiki\**\*.md" LogicalName="docs/wiki/%(RecursiveDir)%(Filename)%(Extension)" />
</ItemGroup>
```

The `LogicalName` is required: without it MSBuild mangles folder names and joins them with dots. To copy a folder
instead, see [Shipping the documents](/mcp/host-docs/#shipping-the-documents).

## 3. Register the documents

In the gateway:

```csharp
builder.Services.AddBeaconMcpGateway(builder.Configuration, x =>
{
    // ...
    x.Docs(y => y.FromEmbeddedResources(typeof(Program).Assembly, "docs/wiki/"));
});
```

The gateway sets the project name for you and syncs the host project the first time it is needed. You add no startup
call.

In full Beacon:

```csharp
var beacon = builder.Services.AddBeaconServices(builder.Configuration, o => { /* your options */ });

beacon.ExposeDocs(o =>
{
    o.ProjectName = "Back Office";                       // the project every Expose* call of this host uses
    o.FromEmbeddedResources(typeof(Program).Assembly, "docs/wiki/");
    // or o.FromDirectory("docs/wiki").Exclude("drafts/**");
});

beacon.UseSqlServer();                                   // ends the builder chain: call ExposeDocs before it

var app = builder.Build();
// ... apply migrations ...
await app.Services.SyncBeaconHostAsync();                // data sources first, then documents
```

`SyncBeaconHostAsync` is idempotent: a document whose content hash is unchanged is left alone, a changed one is
updated and re-chunked, a removed file is archived. It is safe to run from several replicas at once.

## 4. Expose the data model (full Beacon only)

Skip this step in the gateway: it has no data sources, so there is nothing for `ExposeDbContext` to attach to and no
SQL tool to ground.

```csharp
beacon.ExposeDbContext<ShopDbContext>(o =>
{
    o.Name = "Shop";
    o.ProjectName = "Back Office";                       // the same project as ExposeDocs
    o.ReadOnlyConnection("BeaconReadOnly")               // ConnectionStrings:BeaconReadOnly, a read-only login
     .AllowTables("Loan", "LoanInstallment", "Customer")
     .ExcludeColumns(x => x.Table == "Customer" && x.Name.StartsWith("Kyc"))
     .MaskColumns(x => x.Name is "SSN" or "AccountNumber");
});
```

Beacon reads the tables, columns, keys and foreign keys from the EF model without opening a connection, and takes
descriptions from `HasComment` or, with `GenerateDocumentationFile` on, from your XML `<summary>` comments. Write
those comments in business words: they become the column and table descriptions the agent reads. Columns whose names
contain `password`, `secret`, `token` and similar words are excluded whatever you write. See
[Host DbContext](/mcp/host-dbcontext/) for every option.

## 5. How it reaches agents

| Tool | What the agent gets from your material |
|---|---|
| `search` | Documents as `[IMPORTED_DOC]` hits with their path and title, found by keyword, and by meaning when embeddings are enabled. Tables and columns too, in full Beacon. |
| `get_documentation` | The project-level answer ends with a list of your documents. `document` takes a path or an exact title and returns that document in full. `datasource_name`, `table_name` and `schema_name` narrow to the data model. |
| `get_context` | `format: "agents_md"` returns a project brief: data sources, key tables, glossary terms, document titles and how to use the tools. Built from the catalog, with no LLM call. |
| `ask` (full Beacon) | A conceptual question is answered from project documentation, including your documents; a data question is grounded in the schema, sample values, glossary and verified examples. |
| `get_query_context` (full Beacon) | The same grounding context `ask` would use, so an agent can write its own SQL. |

Documents work with no LLM and no embedder: keyword search matches title, path and chunk text. Embeddings, when
configured, add meaning-based matching ([Semantic retrieval](/mcp/knowledge-base/#semantic-retrieval)).

## 6. Teach the vocabulary and verified answers (full Beacon only)

Two kinds of grounding are curated by an Admin after the data source exists:

- **Business glossary**: terms, synonyms and a metric expression, through `/beacon/api/glossary` (API only, no page).
  The top matches (`GlossaryTopK`, default 5) enter the prompt. See [Business glossary](/mcp/knowledge-base/#business-glossary).
- **Golden examples**: human-verified question to SQL pairs. An Admin promotes an answer rated `correct` on the
  **MCP Learning** page (`/beacon/mcp-learning`, tab **Golden candidates**). The top matches (`GoldenExemplarTopK`,
  default 4) enter the prompt as authoritative. See [Golden examples](/mcp/knowledge-base/#golden-examples-and-the-eval-harness).

Both are scoped to the project, so another project that shares the data source never sees them.

## Check it works

- [ ] Startup completes. In full Beacon `SyncBeaconHostAsync` ran after migrations.
- [ ] `GET /beacon/api/projects/{id}/imported-documents` returns your documents (needs Read in the project).
- [ ] Connect an MCP client ([Connect Claude Code, Cursor or VS Code](/cookbooks/connect-coding-agent/)) and say this:

```text wrap title="Say to Claude"
Search the Back Office project for "active customer" and show me which documents match.
Then read the best match in full with get_documentation.
```

- [ ] `search` answers with an `[IMPORTED_DOC]` line for your page, and `get_documentation` with `document` set to its
      path returns the text.
- [ ] Full Beacon only: `get_documentation` with `table_name` returns the descriptions you wrote, and masked columns are
      flagged as masked.
- [ ] Change one sentence in a document, redeploy, and the new text comes back from `get_documentation`.

## If something goes wrong

| Symptom | Cause and fix |
|---|---|
| No documents are listed | The sync has not run (full Beacon: call `SyncBeaconHostAsync` after migrations), the folder is wrong, or the glob matched nothing. A configured directory that does not exist fails startup. |
| Folder names look like `_40_web` or the path has dots | The embedded resources have no `LogicalName`. Set it as in step 2. |
| A file is missing and the log has a size warning | It is over `MaxDocumentBytes` (512 KB). Split it. |
| Two files with names that differ only by case, one missing | Paths compare ignoring case; the second is skipped with a warning naming its path. Rename one. |
| The project is called `Back Office (host)` | A project that is not host-managed already had that name. The host project is created beside it. See [The host project](/mcp/host-docs/#the-host-project). |
| `search` finds nothing for a paraphrase | Embeddings are off, so matching is by keyword. Use the people's own words in the document or enable embeddings. |
| A table is not visible to the agent | It is not in `AllowTables` / `AllowEntity`, or the caller cannot read the project. |
| A query on a host data source is refused for an unqualified table | Host data source SQL must be a single `SELECT` with schema-qualified table names, such as `dbo.Customer`. |

## Next

- [Host docs](/mcp/host-docs/): every option, the sync rules and the REST routes for imported documents.
- [Host DbContext](/mcp/host-dbcontext/): the allow-list, masking and the execution-time policy.
- [Knowledge base and SQL grounding](/mcp/knowledge-base/): what goes into an `ask` prompt and who curates it.
- [Ask your data questions](/cookbooks/ask-your-data/): use the grounding you just built.
