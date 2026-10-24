using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Beacon.AI.Services.Embeddings;

/// <summary>
/// Local, in-process embedding service backed by an ONNX bge-small-en-v1.5 model and a WordPiece
/// (BERT) tokenizer. Registered as a singleton so the <see cref="InferenceSession"/> is loaded once
/// and reused. No network egress — the model and tokenizer are local files.
/// </summary>
/// <remarks>
/// The <see cref="InferenceSession"/> + tokenizer are lazily loaded on first embed call (thread-safe).
/// When the model/tokenizer files are absent or embeddings are disabled, <see cref="IsAvailable"/> is
/// <c>false</c> and the embed methods throw — callers guard on <see cref="IsAvailable"/> first (SC8).
/// This environment has no model file, so only the model-absent path is exercised by tests; the
/// inference path below is written against the verified ONNX Runtime / Tokenizers API surface.
/// </remarks>
internal sealed class OnnxEmbeddingService : IBeaconEmbeddingService, IDisposable
{
    private const int EmbeddingDimensions = 384;
    private const int MaxTokens = 512;

    private readonly EmbeddingOptions _options;
    private readonly ILogger<OnnxEmbeddingService> _logger;
    private readonly Lazy<LoadedModel> _model;
    private readonly Lazy<string> _fingerprint;

    public OnnxEmbeddingService(IConfiguration configuration, ILogger<OnnxEmbeddingService> logger)
    {
        _options = configuration.GetSection("Beacon:Embeddings").Get<EmbeddingOptions>() ?? new EmbeddingOptions();
        _logger = logger;
        _model = new Lazy<LoadedModel>(LoadModel, LazyThreadSafetyMode.ExecutionAndPublication);
        _fingerprint = new Lazy<string>(ComputeFingerprint, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int Dimensions => EmbeddingDimensions;

    public bool IsAvailable =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.ModelPath)
        && File.Exists(_options.ModelPath)
        && !string.IsNullOrWhiteSpace(_options.TokenizerPath)
        && File.Exists(_options.TokenizerPath);

    // SHA-256 over the model and tokenizer files, computed once: a swapped or re-exported model changes it.
    public string ModelFingerprint => IsAvailable ? _fingerprint.Value : string.Empty;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var batch = await EmbedBatchAsync([text], ct);
        return batch[0];
    }

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("Embeddings unavailable");
        }

        // ONNX inference is CPU-bound and synchronous — run it off the calling thread.
        return Task.Run<IReadOnlyList<float[]>>(() =>
        {
            var model = _model.Value;
            var vectors = new List<float[]>(texts.Count);
            foreach (var text in texts)
            {
                ct.ThrowIfCancellationRequested();
                vectors.Add(Embed(model, text ?? string.Empty));
            }

            return vectors;
        }, ct);
    }

    public void Dispose()
    {
        if (_model.IsValueCreated)
        {
            _model.Value.Session.Dispose();
        }
    }

    private string ComputeFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in new[] { _options.ModelPath!, _options.TokenizerPath! })
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private LoadedModel LoadModel()
    {
        _logger.LogInformation("Loading ONNX embedding model from {ModelPath}", _options.ModelPath);

        var options = new BertOptions
        {
            LowerCaseBeforeTokenization = true,
        };

        var tokenizer = BertTokenizer.Create(_options.TokenizerPath!, options);
        var session = new InferenceSession(_options.ModelPath!);

        var loaded = new LoadedModel(session, tokenizer);
        WarmUp(loaded);
        return loaded;
    }

    // The first inference pays for kernel setup and memory arenas; do it at load so the first real batch does not.
    private void WarmUp(LoadedModel model)
    {
        try
        {
            Embed(model, "warm up");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ONNX embedding warm-up inference failed; the first embedding will report the error if it persists.");
        }
    }

    private float[] Embed(LoadedModel model, string text)
    {
        var encoded = model.Tokenizer.EncodeToIds(text, addSpecialTokens: true);
        var tokenCount = Math.Min(encoded.Count, MaxTokens);

        var dimensions = new[] { 1, tokenCount };
        var inputIds = new DenseTensor<long>(dimensions, false);
        var attentionMask = new DenseTensor<long>(dimensions, false);
        var tokenTypeIds = new DenseTensor<long>(dimensions, false);
        for (var i = 0; i < tokenCount; i++)
        {
            inputIds.Buffer.Span[i] = encoded[i];
            attentionMask.Buffer.Span[i] = 1;
            tokenTypeIds.Buffer.Span[i] = 0;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
        };

        // Some bge exports omit token_type_ids — only supply it when the model declares the input.
        if (model.Session.InputMetadata.ContainsKey("token_type_ids"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));
        }

        using var results = model.Session.Run(inputs);
        var hidden = results.First().AsTensor<float>();

        // last_hidden_state is row-major [1, tokenCount, EmbeddingDimensions]. bge-small-en-v1.5 is trained
        // for CLS pooling: the sentence embedding is the [CLS] token's hidden state (position 0), not a mean
        // over tokens. Mean pooling would place the vector in a different region than the model was calibrated
        // for and degrade retrieval quality. Read token 0's row only (not the whole tensor), then L2-normalize.
        // A sentence-embedding export is already pooled: rank 2, [1, EmbeddingDimensions].
        var pooled = new float[EmbeddingDimensions];
        for (var i = 0; i < EmbeddingDimensions; i++)
        {
            pooled[i] = hidden.Rank == 2 ? hidden[0, i] : hidden[0, 0, i];
        }

        Normalize(pooled);
        return pooled;
    }

    private static void Normalize(float[] vector)
    {
        double sumSquares = 0;
        foreach (var value in vector)
        {
            sumSquares += (double)value * value;
        }

        var norm = Math.Sqrt(sumSquares);
        if (norm <= 0)
        {
            return;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }
    }

    private sealed record LoadedModel(InferenceSession Session, BertTokenizer Tokenizer);
}
