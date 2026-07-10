using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Beacon.AI.Services.Embeddings;

namespace Beacon.Tests.Unit;

[TestFixture]
public class OnnxEmbeddingServiceTests
{
    private static OnnxEmbeddingService Create(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        // NullLogger avoids a Castle proxy over ILogger<OnnxEmbeddingService> (internal generic arg).
        return new OnnxEmbeddingService(configuration, NullLogger<OnnxEmbeddingService>.Instance);
    }

    [Test]
    public void Dimensions_Is384()
    {
        var service = Create([]);

        service.Dimensions.Should().Be(384);
    }

    [Test]
    public void IsAvailable_WhenDisabled_IsFalseAndDoesNotThrow()
    {
        var service = Create(new Dictionary<string, string?>
        {
            ["Beacon:Embeddings:Enabled"] = "false",
        });

        service.IsAvailable.Should().BeFalse();
    }

    [Test]
    public void IsAvailable_WhenEnabledButModelFileMissing_IsFalse()
    {
        // SC8: enabled, but the model/tokenizer paths do not exist → unavailable, no throw.
        var service = Create(new Dictionary<string, string?>
        {
            ["Beacon:Embeddings:Enabled"] = "true",
            ["Beacon:Embeddings:ModelPath"] = "/does/not/exist/model.onnx",
            ["Beacon:Embeddings:TokenizerPath"] = "/does/not/exist/vocab.txt",
        });

        service.IsAvailable.Should().BeFalse();
    }

    [Test]
    public void Construction_WithMissingModel_DoesNotThrow()
    {
        var act = () => Create(new Dictionary<string, string?>
        {
            ["Beacon:Embeddings:Enabled"] = "true",
            ["Beacon:Embeddings:ModelPath"] = "/does/not/exist/model.onnx",
            ["Beacon:Embeddings:TokenizerPath"] = "/does/not/exist/vocab.txt",
        });

        act.Should().NotThrow();
    }

    [Test]
    public async Task EmbedAsync_WhenUnavailable_ThrowsInvalidOperationException()
    {
        var service = Create(new Dictionary<string, string?>
        {
            ["Beacon:Embeddings:Enabled"] = "false",
        });

        var act = async () => await service.EmbedAsync("hello world", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Embeddings unavailable");
    }

    [Test]
    public async Task EmbedBatchAsync_WhenUnavailable_ThrowsInvalidOperationException()
    {
        var service = Create(new Dictionary<string, string?>
        {
            ["Beacon:Embeddings:Enabled"] = "true",
            ["Beacon:Embeddings:ModelPath"] = "/does/not/exist/model.onnx",
        });

        var act = async () => await service.EmbedBatchAsync(["a", "b"], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Embeddings unavailable");
    }
}
