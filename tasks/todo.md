# tasks/todo.md — batch: mcp-tier0-hardening

- [ ] 1. Scope-gate /beacon/mcp with ExecuteScopePolicyName (test-first in ExecuteScopeWiringTests)
- [ ] 2. Non-leaking error returns in 4 MCP tools (DbException pass-through in query; ILogger; tests)
- [ ] 3. Remove dead wiring (.WithResourcesFromAssembly, session methods)
- [ ] 4. feedback tool in McpPlaygroundService
- [ ] 5. .mcp.json → streamable HTTP root
- [ ] 6. docs/site mcp-server.md accuracy pass
- [ ] 7. ColumnsUsed populated (validator exposes columns; builder setter; ask call sites; tests)
- [ ] Review: pre-commit gate + security-and-hardening (finding 1)
- [ ] Verify: dotnet build WarningLevel=0 + dotnet test + csharp_diagnostics on changed files
