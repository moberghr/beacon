using System.Text.RegularExpressions;
using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Settings;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using McpSettingsEntity = Beacon.Core.Data.Entities.McpSettings;

namespace Beacon.Tests.Unit.Settings;

/// <summary>
/// A process keeps its cached admin and MCP settings only until its next check of the settings stamp (the highest
/// settings history id), at most <see cref="StoredSettingsConvergence.CheckInterval"/> apart: a save made by another
/// process then drops the cached settings, and a change of the admin settings applies the stored base URL and LLM
/// configuration again.
/// </summary>
[TestFixture]
public class StoredSettingsConvergenceTests
{
    private readonly List<AppSettingHistory> _history = [];
    private readonly List<McpSettingsEntity> _mcpRows = [];
    private FakeTimeProvider _time = null!;
    private MemoryCache _cache = null!;
    private Mock<ILlmConfigurationUpdater> _updater = null!;
    private LogRecorder _log = null!;
    private bool _unreadable;

    [SetUp]
    public void SetUp()
    {
        _history.Clear();
        _mcpRows.Clear();
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero));
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        _updater = new Mock<ILlmConfigurationUpdater>();
        _log = new LogRecorder();
        _unreadable = false;
        McpSettingsProvider.ResetCache();
        AppSettingsService.ResetCache();
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
        McpSettingsProvider.ResetCache();
    }

    [Test]
    public async Task AnAdminSettingsChangeStoredElsewhere_IsPickedUpAtTheNextCheck_AndApplied()
    {
        var convergence = Convergence();
        _history.Add(History(1, "LLM.Model"));
        await convergence.EnsureCurrentAsync(CancellationToken.None);
        await AdminSettings().GetSettingsAsync(CancellationToken.None);
        _history.Add(History(2, "LLM.ApiKey"));

        await convergence.EnsureCurrentAsync(CancellationToken.None);

        _cache.TryGetValue(AppSettingsService.CacheKey, out _).Should().BeTrue("the change is not seen before the next check");
        _updater.Verify(x => x.UpdateConfiguration(It.IsAny<AppSettingsData>(), It.IsAny<bool>()), Times.Never);

        _time.Advance(StoredSettingsConvergence.CheckInterval);
        await convergence.EnsureCurrentAsync(CancellationToken.None);

        _cache.TryGetValue(AppSettingsService.CacheKey, out _).Should().BeFalse();
        _updater.Verify(x => x.UpdateConfiguration(It.Is<AppSettingsData>(y => y.LlmProvider == AiProvider.OpenAI), false), Times.Once);
    }

    [Test]
    public async Task AnMcpSettingsChangeStoredElsewhere_ReplacesTheCachedMcpSettings_AndLeavesTheLlmConfiguration()
    {
        var convergence = Convergence();
        var provider = McpProvider(convergence);
        _mcpRows.Add(new McpSettingsEntity { Id = 1, MaxRowLimit = 100 });
        (await provider.GetSettingsAsync(CancellationToken.None)).MaxRowLimit.Should().Be(100);
        _mcpRows[0].MaxRowLimit = 250;
        _history.Add(History(7, "Mcp.MaxRowLimit"));

        (await provider.GetSettingsAsync(CancellationToken.None)).MaxRowLimit.Should().Be(100, "the cached copy stands until the next check");

        _time.Advance(StoredSettingsConvergence.CheckInterval);

        (await provider.GetSettingsAsync(CancellationToken.None)).MaxRowLimit.Should().Be(250);
        _updater.Verify(x => x.UpdateConfiguration(It.IsAny<AppSettingsData>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task AnUnchangedStamp_KeepsTheCachedSettings()
    {
        var convergence = Convergence();
        _history.Add(History(3, "LLM.Model"));
        await convergence.EnsureCurrentAsync(CancellationToken.None);
        await AdminSettings().GetSettingsAsync(CancellationToken.None);

        _time.Advance(StoredSettingsConvergence.CheckInterval);
        await convergence.EnsureCurrentAsync(CancellationToken.None);

        _cache.TryGetValue(AppSettingsService.CacheKey, out _).Should().BeTrue();
        _updater.Verify(x => x.UpdateConfiguration(It.IsAny<AppSettingsData>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task AStampThatCannotBeRead_KeepsTheCachedSettings_AndIsLoggedByExceptionType()
    {
        var convergence = Convergence();
        await AdminSettings().GetSettingsAsync(CancellationToken.None);
        _unreadable = true;

        var check = () => convergence.EnsureCurrentAsync(CancellationToken.None);

        await check.Should().NotThrowAsync();
        _cache.TryGetValue(AppSettingsService.CacheKey, out _).Should().BeTrue();
        _log.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning)
            .Which.Message.Should().Contain(nameof(InvalidOperationException)).And.NotContain("unreadable-detail");
    }

    [Test]
    public async Task TheAdminSettings_AreCachedUnderABeaconKey_InACacheWithASizeLimit()
    {
        await AdminSettings().GetSettingsAsync(CancellationToken.None);

        _cache.Keys.Should().ContainSingle().Which.Should().BeOfType<string>().Which.Should().StartWith(BeaconCacheEntries.KeyPrefix);
    }

    [Test]
    public async Task TheMcpSettings_AreCachedUnderBeaconKeys_InACacheWithASizeLimit()
    {
        var provider = McpProvider(null);

        await provider.GetEffectiveSettingsAsync(5, CancellationToken.None);

        _cache.Keys.Should().HaveCount(2).And.OnlyContain(x => x is string && ((string)x).StartsWith(BeaconCacheEntries.KeyPrefix, StringComparison.Ordinal));
    }

    [Test]
    public void EveryCacheEntryBeaconWrites_HasABeaconKeyAndASize()
    {
        var root = RepositoryRoot();
        var files = new[] { "Beacon.Core", "Beacon.AI", "Beacon.MCP", "Beacon.Api" }
            .SelectMany(x => Directory.GetFiles(Path.Combine(root, "src", x), "*.cs", SearchOption.AllDirectories))
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        var writes = new Regex(@"[cC]ache\.(Set|GetOrCreate|GetOrCreateAsync|CreateEntry)\(");

        var unsized = files
            .SelectMany(x => File.ReadAllLines(x).Select(y => (File: Path.GetFileName(x), Line: y)))
            .Where(x => writes.IsMatch(x.Line))
            .Where(x => !x.Line.Contains("BeaconCacheEntries.For(", StringComparison.Ordinal))
            .Where(x => !x.Line.Contains("GetOrCreate", StringComparison.Ordinal) || !File.ReadAllText(files.First(y => Path.GetFileName(y) == x.File)).Contains("entry.Size = 1", StringComparison.Ordinal))
            .Select(x => $"{x.File}: {x.Line.Trim()}")
            .ToList();

        unsized.Should().BeEmpty("a host that sets a cache size limit refuses entries without a size");
        files.Should().Contain(x => File.ReadAllText(x).Contains("BeaconCacheEntries.For(", StringComparison.Ordinal), "the scan reads Beacon's source");
    }

    private StoredSettingsConvergence Convergence()
    {
        var settings = new Mock<IAppSettingsService>();
        settings
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettingsData { LlmProvider = AiProvider.OpenAI, BaseUrl = "https://beacon.example" });
        var services = new ServiceCollection();
        services.AddSingleton(settings.Object);
        services.AddSingleton(new BeaconConfiguration());
        services.AddSingleton(_updater.Object);
        services.AddSingleton<ILogger<StoredSettingsApplier>>(NullLogger<StoredSettingsApplier>.Instance);
        services.AddTransient<StoredSettingsApplier>();
        var provider = services.BuildServiceProvider();

        return new StoredSettingsConvergence(
            Factory(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            _time,
            _log.For<StoredSettingsConvergence>());
    }

    private AppSettingsService AdminSettings()
    {
        return new AppSettingsService(Factory(), _cache, new EncryptionService(TestEncryptionKey.Value), new BeaconConfiguration(), new RecordingSecurityAudit());
    }

    private McpSettingsProvider McpProvider(StoredSettingsConvergence? convergence)
    {
        return new McpSettingsProvider(
            Factory(),
            _cache,
            Options.Create(new McpDeploymentOptions()),
            NullLogger<McpSettingsProvider>.Instance,
            convergence);
    }

    private IDbContextFactory<BeaconContext> Factory()
    {
        var saved = new List<object>();
        var sets = new Dictionary<Type, object>
        {
            [typeof(AppSettingHistory)] = RecordingBeaconContext.MemorySet(_history, saved),
            [typeof(AppSetting)] = RecordingBeaconContext.MemorySet(new List<AppSetting>(), saved),
            [typeof(McpSettingsEntity)] = RecordingBeaconContext.MemorySet(_mcpRows, saved),
            [typeof(McpProjectSettings)] = RecordingBeaconContext.MemorySet(new List<McpProjectSettings>(), saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns(() => _unreadable
                ? throw new InvalidOperationException("unreadable-detail")
                : Task.FromResult<BeaconContext>(new RecordingBeaconContext(sets, saved)));

        return factory.Object;
    }

    private static AppSettingHistory History(int id, string key)
    {
        return new AppSettingHistory
        {
            Id = id,
            SettingKey = key,
            ChangedAt = new DateTime(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc)
        };
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Beacon.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
