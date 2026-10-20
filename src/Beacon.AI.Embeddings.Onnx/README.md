# Moberg.Beacon.AI.Embeddings.Onnx

Local, in-process text embeddings for Beacon's semantic retrieval, backed by an ONNX model
(bge-small-en-v1.5, 384 dimensions) and a WordPiece tokenizer. Nothing leaves your process. It is an
optional add-on to `Moberg.Beacon.AI`: without it, `AddBeaconAI` registers a disabled embedding service
and retrieval stays keyword-based.

## Install

```bash
dotnet add package Moberg.Beacon.AI.Embeddings.Onnx
```

## Register

Call `AddBeaconOnnxEmbeddings()` before or after `AddBeaconAI`; it replaces the disabled default either way.

```csharp
using Beacon.AI;

builder.Services.AddBeaconAI(builder.Configuration);
builder.Services.AddBeaconOnnxEmbeddings();
```

## Configure

Beacon does not ship the model files. Download them and mount them read-only.

```json
{
  "Beacon": {
    "Embeddings": {
      "Enabled": true,
      "ModelPath": "/models/bge-small-en-v1.5/model.onnx",
      "TokenizerPath": "/models/bge-small-en-v1.5/vocab.txt"
    }
  }
}
```

| Key | Default | What it does |
|---|---|---|
| `Enabled` | `false` | Turns local embeddings on. |
| `ModelPath` | none | The ONNX model file. |
| `TokenizerPath` | none | The WordPiece vocabulary file. |

Embeddings are used only when `Enabled` is `true` and both files exist. Otherwise Beacon falls back to
keyword retrieval.
