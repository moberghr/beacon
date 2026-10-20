using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Beacon.AI;
using Beacon.AI.Services.Embeddings;

namespace Beacon.Tests.Unit;

[TestFixture]
public class EmbeddingRegistrationTests
{
    private static (ServiceCollection Services, IConfiguration Configuration, CapturingLoggerProvider Logs) Create(bool? enabled)
    {
        var settings = new Dictionary<string, string?>();
        if (enabled.HasValue)
        {
            settings["Beacon:Embeddings:Enabled"] = enabled.Value.ToString();
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(x => x.AddProvider(logs));

        return (services, configuration, logs);
    }

    [Test]
    public void AddBeaconAI_alone_registers_the_disabled_service()
    {
        var (services, configuration, _) = Create(null);
        services.AddBeaconAI(configuration);

        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IBeaconEmbeddingService>();

        service.Should().BeOfType<DisabledEmbeddingService>();
        service.IsAvailable.Should().BeFalse();
        service.Dimensions.Should().Be(384);
    }

    [Test]
    public async Task Disabled_service_embed_methods_throw_naming_the_package_and_call()
    {
        var (services, configuration, _) = Create(null);
        services.AddBeaconAI(configuration);

        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IBeaconEmbeddingService>();

        var single = () => service.EmbedAsync("text", CancellationToken.None);
        var batch = () => service.EmbedBatchAsync(["text"], CancellationToken.None);

        (await single.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Moberg.Beacon.AI.Embeddings.Onnx").And.Contain("AddBeaconOnnxEmbeddings()");
        (await batch.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Moberg.Beacon.AI.Embeddings.Onnx").And.Contain("AddBeaconOnnxEmbeddings()");
    }

    [Test]
    public void Onnx_after_AddBeaconAI_replaces_the_disabled_service()
    {
        var (services, configuration, _) = Create(null);
        services.AddBeaconAI(configuration);
        services.AddBeaconOnnxEmbeddings();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IBeaconEmbeddingService>().Should().BeOfType<OnnxEmbeddingService>();
        services.Count(x => x.ServiceType == typeof(IBeaconEmbeddingService)).Should().Be(1);
    }

    [Test]
    public void Onnx_before_AddBeaconAI_is_not_overridden_by_the_disabled_default()
    {
        var (services, configuration, _) = Create(null);
        services.AddBeaconOnnxEmbeddings();
        services.AddBeaconAI(configuration);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IBeaconEmbeddingService>().Should().BeOfType<OnnxEmbeddingService>();
        services.Count(x => x.ServiceType == typeof(IBeaconEmbeddingService)).Should().Be(1);
    }

    [Test]
    public void Warning_is_logged_exactly_once_when_enabled_without_a_registered_implementation()
    {
        var (services, configuration, logs) = Create(true);
        services.AddBeaconAI(configuration);

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IBeaconEmbeddingService>();
        _ = provider.GetRequiredService<IBeaconEmbeddingService>();

        var warnings = logs.Entries.Where(x => x.Level == LogLevel.Warning).ToList();
        warnings.Should().ContainSingle();
        warnings[0].Message.Should().Contain("Moberg.Beacon.AI.Embeddings.Onnx").And.Contain("AddBeaconOnnxEmbeddings()");
    }

    [TestCase(false)]
    [TestCase(null)]
    public void No_warning_is_logged_when_embeddings_are_not_enabled(bool? enabled)
    {
        var (services, configuration, logs) = Create(enabled);
        services.AddBeaconAI(configuration);

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IBeaconEmbeddingService>();

        logs.Entries.Where(x => x.Level == LogLevel.Warning).Should().BeEmpty();
    }

    [Test]
    public void No_warning_is_logged_when_the_onnx_service_is_registered()
    {
        var (services, configuration, logs) = Create(true);
        services.AddBeaconAI(configuration);
        services.AddBeaconOnnxEmbeddings();

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IBeaconEmbeddingService>();

        logs.Entries.Where(x => x.Level == LogLevel.Warning).Should().BeEmpty();
    }

    [Test]
    public void Obsolete_AddBeaconEmbeddings_registers_the_disabled_service()
    {
        var (services, _, _) = Create(null);
#pragma warning disable CS0618
        services.AddBeaconEmbeddings();
#pragma warning restore CS0618

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IBeaconEmbeddingService>().Should().BeOfType<DisabledEmbeddingService>();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingLogger(Entries);
        }

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(List<(LogLevel Level, string Message)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
