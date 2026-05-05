# Git Workflow

> §8.x — Branches, commits, PRs. Loaded automatically by Claude Code.

§8.1 **Branch naming uses `/` for hierarchy:** `feature/calculator-multiplication`, `fix/login-redirect`, `ai/workflow-redesign`. The leading segment categorizes the work; the trailing segment names it. NEVER `feature_calculator-multiplication`, `feature-calculator`, or a bare branch name.

§8.2 **Commit messages are short, lowercase, descriptive.** No `feat:` / `fix:` / `chore:` conventional-commit prefixes. Recent log style: `mudblazor 9 and redesign`, `renamed to beacon`, `added sso login`.

§8.3 **`dotnet build --property WarningLevel=0` and `dotnet test` MUST pass before commit.** Fix compilation errors before reporting completion.

§8.4 **NEVER bypass hooks.** Do not pass `--no-verify` to `git commit` without explicit user approval. The pre-commit hook runs the deterministic linter — its findings are blocking by design.

§8.5 **After refactoring or multi-file edits, verify all `using` directives and namespaces are intact.** Use the C# LSP `csharp_diagnostics` tool on every changed file before claiming completion.
