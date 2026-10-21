# Moberg.Beacon.AI

Beacon's AI layer: the LLM providers and request queue, natural-language questions to SQL, AI-generated
documentation, AI actors and MCP evaluations. It is optional, except that `Moberg.Beacon.MCP`
depends on it.

## Install

```bash
dotnet add package Moberg.Beacon.AI
```

## Register

Turn the AI features on with `options.UseAI = true` and register the services with `AddBeaconAI`. You need both:
`UseAI` alone registers nothing from this package.

```csharp
using Beacon.AI;
using Beacon.Core;
using Beacon.Core.PostgreSql;

builder.Services
    .AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<MyBeaconScheduler>();
        options.UseAI = true;
    })
    .UsePostgreSql();

builder.Services.AddBeaconAI(builder.Configuration);
```

## Configure a provider

`AddBeaconAI` reads the `Beacon:LLM` section once at startup. The section is optional: an Admin can set or change
the provider later in **Admin settings** (`/beacon/admin-settings`), and the new provider takes effect without a
restart. Put the API key in an environment variable or secret store (`Beacon__LLM__ApiKey`), not in a committed
file.

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "Claude",
      "Model": "<model id>"
    }
  }
}
```

| Key | Default | What it does |
|---|---|---|
| `Provider` | `OpenAI` | `OpenAI`, `Claude`, `AzureOpenAI` or `Bedrock`. `Bedrock` needs the `Moberg.Beacon.AI.Bedrock` package and `AddBeaconBedrock()`. |
| `ApiKey` | empty | Provider key. For Bedrock: empty uses the default AWS credential chain; `accessKey:secretKey` uses those keys. |
| `Model` | empty | Model or deployment name. |
| `FastModel` | none | Model for cheaper calls. Falls back to a provider default, or to `Model` on Azure OpenAI and Bedrock. |
| `Endpoint` | none | Azure OpenAI resource endpoint. Required for `AzureOpenAI`. |
| `Region` | none | AWS region. Required for `Bedrock`. |
| `SessionToken` | none | Bedrock session token, used with `accessKey:secretKey`. |
| `Limits:MaxConcurrentRequests` | `50` | Concurrent LLM calls allowed by the request queue. Fixed at startup. |

Every LLM call goes through that queue. With no `ApiKey` (and a provider other than Bedrock), Beacon starts with
no active provider until one is configured. A provider whose package is not installed is never activated: Beacon
logs a warning and starts with no active provider.

## Documentation

- [AI providers](https://moberghr.github.io/beacon/integrate/ai/)
- [Host setup](https://moberghr.github.io/beacon/integrate/host-setup/)
- [Packages](https://moberghr.github.io/beacon/integrate/packages/) and the [integration overview](https://moberghr.github.io/beacon/integrate/)
- For coding agents: [llms-integrate.txt](https://moberghr.github.io/beacon/llms-integrate.txt)

## License

Dual-licensed under AGPL-3.0 or a commercial license. See
[LICENSING.md](https://github.com/moberghr/beacon/blob/main/LICENSING.md).
