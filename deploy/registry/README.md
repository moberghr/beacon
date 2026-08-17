# MCP Registry publishing runbook

`server.json` is Beacon's manifest for the official MCP registry
([registry.modelcontextprotocol.io](https://registry.modelcontextprotocol.io)). **This repo ships
the manifest only — publishing is a deliberate, out-of-band human decision** (it requires DNS
verification of `moberg.hr` and makes the deployment URL public).

## Prerequisites

- A production Beacon deployment reachable over public HTTPS. The `remotes[0].url` in
  `server.json` is a **placeholder** (`https://beacon.moberg.hr/beacon/mcp`) — replace it with the
  real public MCP endpoint before publishing.
- The deployment must serve the discovery documents this manifest implies:
  - `GET /.well-known/oauth-protected-resource` (RFC 9728 metadata)
  - `GET /.well-known/mcp/server-card.json` (server card)
  - `401` responses from `/beacon/mcp` carrying `WWW-Authenticate: Bearer resource_metadata="…"`.
- Set `Beacon:PublicBaseUrl` on the deployment so the discovery documents advertise the public
  origin rather than the reverse proxy's internal host. When it is unset, Beacon logs a startup
  warning and reflects the request `Host` header into discovery documents and challenges — do not
  publish a deployment in that state.
- The `mcp-publisher` CLI:

  ```bash
  brew install mcp-publisher
  # or download a release: https://github.com/modelcontextprotocol/registry/releases
  ```

## Publish

1. **Update the manifest.** Set `remotes[0].url` to the real endpoint and bump `version` to match
   the deployed server version (single-sourced in `src/Beacon.MCP/Discovery/McpDiscoveryDocuments.cs`).

2. **Authenticate the `hr.moberg/*` namespace via DNS.** The reverse-DNS name `hr.moberg/beacon`
   requires proof of control over `moberg.hr`:

   ```bash
   mcp-publisher login dns --domain moberg.hr --private-key <hex-seed>
   ```

   The command prints the exact `TXT` record to create (shape:
   `moberg.hr. IN TXT "v=MCPv1; k=ed25519; p=<public-key>"`). Create it with the DNS provider,
   wait for propagation, then re-run the login. (`mcp-publisher login http --domain moberg.hr` is
   the alternative if serving a well-known file on `https://moberg.hr` is easier than DNS.)

3. **Publish:**

   ```bash
   cd deploy/registry
   mcp-publisher publish
   ```

4. **Verify:**

   ```bash
   curl -s "https://registry.modelcontextprotocol.io/v0/servers?search=hr.moberg/beacon"
   ```

## Updating

Re-run step 3 with a bumped `version` whenever the deployed server version changes. The registry
treats each version as immutable — never republish an existing version with different contents.
