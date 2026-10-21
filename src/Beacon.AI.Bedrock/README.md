# Moberg.Beacon.AI.Bedrock

The Amazon Bedrock LLM provider for Beacon. It is an optional add-on to `Moberg.Beacon.AI`, which ships the
OpenAI, Azure OpenAI and Claude providers. Without this package a stored `Bedrock` provider is never activated:
Beacon logs a warning and runs with no active LLM provider.

## Install

```bash
dotnet add package Moberg.Beacon.AI.Bedrock
```

## Register

Call `AddBeaconBedrock()` before or after `AddBeaconAI`. Calling it more than once registers it once.

```csharp
using Beacon.AI;

builder.Services.AddBeaconAI(builder.Configuration);
builder.Services.AddBeaconBedrock();
```

## Configure

Choose Bedrock in **Admin settings**, or in the `Beacon:LLM` section. Bedrock needs no API key.

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "Bedrock",
      "Region": "eu-central-1",
      "Model": "<bedrock model id>"
    }
  }
}
```

| Key | Default | What it does |
|---|---|---|
| `Region` | none | AWS region. Required. |
| `Model` | empty | Bedrock model id. |
| `FastModel` | none | Model id for cheaper calls. Falls back to `Model`. |
| `BedrockAuthMode` | `IamRole` | `IamRole` uses the default AWS credential chain (instance role, environment). `AccessKey` uses `AwsAccessKeyId` and `AwsSecretAccessKey`. `TemporaryCredentials` uses those plus `SessionToken`. |

Keep the keys in an environment variable or a secret store, not in a committed file. An older install that stored
the keys as `accessKey:secretKey` in `ApiKey` keeps working when both key fields are empty; in the `IamRole` mode
Beacon then logs a warning, because the static keys are used instead of the default credential chain.

## Documentation

- [AI providers](https://moberghr.github.io/beacon/integrate/ai/)
- [Packages](https://moberghr.github.io/beacon/integrate/packages/)
