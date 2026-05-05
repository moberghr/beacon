# Coding Style

> §3.x — Project-load-bearing deltas from the Moberg coding guidelines. Full guide at `.claude/references/dotnet/coding-guidelines.md`.

These are the rules most often violated in this repo. They are NOT the full guide — read the full guide for naming, layout, and other conventions.

## LINQ & lambdas

§3.1 **Lambda parameter is `x`** — inner lambdas use `y`, then `z`. Never `entity =>`, `e =>`, `c =>`.

§3.2 **Chain LINQ methods on separate lines.** Each `.Where()`, `.Select()`, `.OrderBy()`, etc. on its own line.

§3.3 **Multiple `&&` conditions become multiple `.Where()` calls.**

```csharp
// Bad
.Where(x => x.Status == Active && x.CreatedTime > cutoff)

// Good
.Where(x => x.Status == Active)
.Where(x => x.CreatedTime > cutoff)
```

§3.4 **`.Where()` before `.FirstOrDefault()` / `.SingleOrDefault()`** — never put the predicate inside the terminal call.

§3.5 **`new` keyword on its own line in `.Select` projections**, indented one level deeper than `.Select(`:

```csharp
.Select(x =>
    new ProjectListItem
    {
        Id = x.Id,
        Name = x.Name,
        DataSourceCount = x.DataSources.Count
    })
.ToListAsync(cancellationToken);
```

§3.6 **Use async LINQ for all DB queries.** `ToListAsync()`, `FirstOrDefaultAsync()`, `CountAsync()` — always with `CancellationToken`.

§3.7 **`foreach` over LINQ `.ForEach()`.** Never `.ToList().ForEach(...)`.

§3.8 **Project only what you use.** `.Select(new ...)` projection — do NOT add `.Include()` alongside; EF auto-joins for projection.

§3.9 **`FirstOrDefault` for primary-key lookups, never `Single`.** PK uniqueness is a DB invariant; the duplicate case can't happen.

## Layout & control flow

§3.10 **File-scoped namespaces** — `namespace Beacon.Core.Handlers.Queries;` (no braces).

§3.11 **`var` for all locals.** Never explicit type for a local that has an obvious initializer.

§3.12 **Always braces** on `if`, `else`, `while`, `for`, `foreach` — even single-line bodies.

§3.13 **No `else` after a `return` or `throw`** — return early, happy path stays at the outer indent.

```csharp
if (folder == null)
{
    throw new InvalidOperationException("Folder not found.");
}

// Happy path here, no else
```

§3.14 **`using var` declaration form**, not `using (...) { }` block form.

§3.15 **Object initializer without `()`** — `new Person { Name = "..." }`, not `new Person() { Name = "..." }`. Assign all properties in the initializer; do not set properties after construction.

§3.16 **Add `.Add()` / `.AddAsync()` calls just before `SaveChangesAsync()`** — not earlier in the method.

§3.17 **Declare variables close to first use.** No "all locals at top of method" block.

§3.18 **Private methods last in file.**

§3.19 **Single blank line between logical blocks.** Never two blank lines in a row.

§3.20 **No `this.` prefix** on field access.

§3.21 **No meaningless XML doc comments** (`/// <param name="x">the x parameter</param>`). Either a useful doc or none.

§3.22 **Ternary for simple assignments**, `if`/`else` only when bodies are non-trivial.

§3.23 **`??` (null-coalescing) over `x != null ? x : fallback`.**

§3.24 **`foreach` on a materialized variable** — never call `.ToList()` inside a `foreach` header.
