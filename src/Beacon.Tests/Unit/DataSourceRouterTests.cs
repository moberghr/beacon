using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Models.Ai;

namespace Beacon.Tests.Unit;

/// <summary>
/// <see cref="DataSourceRouter"/> keeps only the project's own data sources the model names, each once in the model's
/// order, and at most four of them; with one data source it asks the model nothing.
/// </summary>
[TestFixture]
public class DataSourceRouterTests
{
    private Mock<ILlmProvider> _llm = null!;
    private StructuredLogger _log = null!;
    private RoutingResult _result = null!;

    [SetUp]
    public void SetUp()
    {
        _llm = new Mock<ILlmProvider>();
        _log = new StructuredLogger();
    }

    [Test]
    public async Task RouteAsync_RepeatedIds_AreKeptOnceInTheModelsOrder()
    {
        var result = await RouteAsync(SourcesWithIds(1, 2, 3, 4, 5), [3, 3, 5, 3]);

        result.Should().Equal(3, 5);
        _log.Entries.Should().BeEmpty();
    }

    [Test]
    public async Task RouteAsync_ExactlyFourSources_KeepsThemAll_AndLogsNothing()
    {
        var result = await RouteAsync(SourcesWithIds(1, 2, 3, 4, 5), [4, 3, 2, 1]);

        result.Should().Equal(4, 3, 2, 1);
        _result.DroppedSourceCount.Should().Be(0);
        _log.Entries.Should().BeEmpty();
    }

    [Test]
    public async Task RouteAsync_FiveSources_KeepsTheFirstFour_ReportsTheFifthDropped_AndLogsTheCounts()
    {
        var result = await RouteAsync(SourcesWithIds(1, 2, 3, 4, 5), [5, 4, 3, 2, 1]);

        result.Should().Equal(5, 4, 3, 2);
        _result.DroppedSourceCount.Should().Be(1);
        var entry = _log.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Values.Should().Contain(new KeyValuePair<string, object?>("Count", 5));
        entry.Values.Should().Contain(new KeyValuePair<string, object?>("Kept", 4));
        entry.Values.Select(x => x.Value).OfType<string>().Should().NotContain(x => x.Contains("Source"), "names never reach the log");
    }

    [Test]
    public async Task RouteAsync_RepeatsAreDroppedBeforeTheCap()
    {
        var result = await RouteAsync(SourcesWithIds(1, 2, 3, 4, 5), [1, 1, 2, 3, 4, 5]);

        result.Should().Equal(1, 2, 3, 4);
        _result.DroppedSourceCount.Should().Be(1);
        _log.Entries.Should().ContainSingle()
            .Which.Values.Should().Contain(new KeyValuePair<string, object?>("Count", 5));
    }

    [Test]
    public async Task RouteAsync_IdsOfAnotherProject_AreStillDropped()
    {
        var result = await RouteAsync(SourcesWithIds(1, 2), [99, 2, 99]);

        result.Should().Equal(2);
    }

    // The ask tool spends a routing call only when there is more than one source: this pins that the router asks nothing then.
    [Test]
    public async Task RouteAsync_OneSource_AsksTheModelNothing()
    {
        var result = await RouteAsync(SourcesWithIds(7), [7]);

        result.Should().Equal(7);
        _llm.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private async Task<List<int>> RouteAsync(List<DataSourceKnowledge> sources, int[] routedIds)
    {
        var entries = string.Join(",", routedIds.Select(x => $$"""{ "datasource_id": {{x}}, "datasource_name": "n", "reason": "r" }"""));
        _llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = $$"""{ "sources": [{{entries}}] }""" });

        _result = await new DataSourceRouter(_log).RouteAsync(_llm.Object, sources, "which orders shipped late?", CancellationToken.None);

        return _result.Sources
            .Select(x => x.DataSourceId)
            .ToList();
    }

    private static List<DataSourceKnowledge> SourcesWithIds(params int[] ids)
    {
        return ids
            .Select(x =>
                new DataSourceKnowledge
                {
                    DataSourceId = x,
                    Name = $"Source {x}"
                })
            .ToList();
    }

    private sealed record StructuredEntry(LogLevel Level, IReadOnlyList<KeyValuePair<string, object?>> Values);

    // Keeps each entry's structured values, so a test asserts the logged counts rather than digits in the message.
    private sealed class StructuredLogger : ILogger<DataSourceRouter>
    {
        public List<StructuredEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            Entries.Add(new StructuredEntry(logLevel, values));
        }
    }
}
