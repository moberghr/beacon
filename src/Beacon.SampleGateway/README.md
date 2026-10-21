# Beacon.SampleGateway

The reference host for Beacon as an MCP gateway (`AddBeaconMcpGateway`): an orders application whose customers ask an AI
client about their own orders and cancel an open one. A sample, never packed or shipped.

Run it with a PostgreSQL that has pgvector available and these user secrets:

```bash
dotnet user-secrets set "Beacon:EncryptionKey" "$(openssl rand -base64 32)"
dotnet user-secrets set "ConnectionStrings:BeaconContext" "Host=localhost;Database=beacon_gateway;Username=..."
dotnet user-secrets set "AzureAd:TenantId" "<tenant id>"
dotnet user-secrets set "AzureAd:ClientId" "<application client id>"
dotnet user-secrets set "Sample:Customers:<your Entra object id>" "1"
dotnet run
```

Then `/signin?customer=1` opens the admin UI at `/beacon` (customer 1 holds the gateway admin policy), and an MCP client
connects to `/beacon/mcp`. See the gateway guide: https://moberghr.github.io/beacon/mcp/gateway/
