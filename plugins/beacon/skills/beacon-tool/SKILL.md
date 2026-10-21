---
name: beacon-tool
description: Expose one endpoint of an ASP.NET Core application as a Beacon MCP tool (api_<name>) safely — self-service, minimal data, authorization metadata, the person's id bound from the caller, an agent-facing description and tests. Use when someone asks to make an endpoint available to Claude through their Beacon MCP gateway, or to add or review a gateway tool.
---

# Mark an endpoint as a Beacon tool

Read the "Mark endpoints as tools" section of `https://moberghr.github.io/beacon/llms-gateway.txt` first. Then, for the
endpoint in question:

1. **Is it self-service?** It must read or change the signed-in person's own records. Refuse to mark an admin endpoint,
   one under an admin or recruitment area, or one gated by a permission a person may hold over others. An admin
   capability for agents is a design discussion, not an attribute — say so.
2. **Does it return other people's data to some callers?** (a manager's view, a queue, a lookup by sequential id). Then do
   not mark it: add a `/api/my/...` sibling that filters on the caller, takes no subject, and reuses the projection.
3. **Authorization metadata:** `[Authorize]` or a policy on the endpoint (Beacon refuses one without, unless the app has a
   fallback policy). Never `[AllowAnonymous]`.
4. **The person's id:** never ask the agent for it. Use the gateway's `BindFromCaller("<argument>", "<claim>")` so the
   argument leaves the schema and carries the caller's own value; do not add "0 means me" logic to the endpoint.
5. **Mark it:** `.WithBeaconTool("snake_name", description, readOnly: true)` on a minimal API (`using Beacon.MCP.HostEndpoints;`),
   or `[BeaconTool("snake_name", Description = "...", ReadOnly = true)]` on the action or the handler class
   (`using Beacon.Core.HostEndpoints;`). Names match `^[a-z][a-z0-9_]{2,47}$`.
6. **The description is for an agent:** what comes back, field by field (with units and time zones); what each argument
   means and what leaving it out does; every refusal it can produce. For a write tool add "Call it only when the person
   asked for this change."
7. **Write tools:** `ReadOnly = false`, with `Destructive` and `Idempotent` stated; the gateway needs
   `AllowWriteTools = true`. Clients ask the person before each call; the audit marks it `:write`.
8. **Tests** (`Moberg.Beacon.Testing`): the tool is listed with exactly the expected arguments, a call as one person
   returns only their data, and a call that names somebody else's record is refused. Add it to the application's pinned
   list of tools if it keeps one.
9. Build, run the tests, and update the application's user-facing docs if it lists what Claude can do.
