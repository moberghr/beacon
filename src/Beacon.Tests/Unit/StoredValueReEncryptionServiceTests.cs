using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Beacon.Api;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.Notifications;

namespace Beacon.Tests.Unit;

/// <summary>
/// Moving stored values off a retired encryption key, as real service code over in-memory sets (no database): every
/// encrypted column (data-source connection data, repository tokens, sensitive settings, recipient destinations and
/// headers, archived rows included) is checked; a value only a previous key decrypts is re-encrypted with the current key,
/// conditional on it being unchanged; values under the current key are left alone; values no key decrypts are counted and
/// logged by id. The count-only run changes nothing, and the startup warning reports its counts.
/// </summary>
[TestFixture]
public class StoredValueReEncryptionServiceTests
{
    private const string CurrentKey = "the key configured now";
    private const string PreviousKey = "the key configured before";

    private readonly EncryptionService _encryption = new(CurrentKey, [PreviousKey]);
    private readonly EncryptionService _previous = new(PreviousKey);
    private LogRecorder _logs = new();

    [SetUp]
    public void SetUp()
    {
        _logs = new LogRecorder();
    }

    [Test]
    public async Task EveryValueUnderAPreviousKey_IsReEncryptedWithTheCurrentKey_ArchivedRowsIncluded()
    {
        var store = new Store();
        store.DataSources.Add(DataSource(1, _previous.Encrypt("Host=old")));
        store.DataSources.Add(DataSource(2, _encryption.Encrypt("Host=current"), archived: true));
        store.DataSources.Add(DataSource(3, _previous.Encrypt("Host=archived"), archived: true));
        store.Repositories.Add(new GitHubRepository { Id = 4, RepositoryUrl = "https://example.test/r", EncryptedAccessToken = _previous.Encrypt("token") });
        store.Settings.Add(new AppSetting { Id = 5, Key = "LLM.ApiKey", Category = "LLM", IsSensitive = true, Value = _previous.Encrypt("llm-key") });
        store.Settings.Add(new AppSetting { Id = 6, Key = "General.BaseUrl", Category = "General", IsSensitive = false, Value = "https://beacon.test" });
        store.Recipients.Add(new Recipient
        {
            Id = 7,
            Name = "hook",
            Destination = RecipientSecretProtector.EncryptedPrefix + _previous.Encrypt("https://hooks.example.test/a"),
            HeadersJson = RecipientSecretProtector.EncryptedPrefix + _previous.Encrypt("{\"X\":\"1\"}")
        });

        var result = await Service(store).ReEncryptAsync(CancellationToken.None);

        result.Should().Be(new StoredValueReEncryptionResult(UnderPreviousKeys: 6, ReEncrypted: 6, SkippedAsChanged: 0, Unreadable: 0, Failed: 0));
        UnderCurrentKeyOnly(store.DataSources[0].EncryptedConnectionData).Should().Be("Host=old");
        UnderCurrentKeyOnly(store.DataSources[2].EncryptedConnectionData).Should().Be("Host=archived");
        UnderCurrentKeyOnly(store.Repositories[0].EncryptedAccessToken!).Should().Be("token");
        UnderCurrentKeyOnly(store.Settings[0].Value!).Should().Be("llm-key");
        store.Settings[1].Value.Should().Be("https://beacon.test", "a setting that is not sensitive is not encrypted");
        store.Recipients[0].Destination.Should().StartWith(RecipientSecretProtector.EncryptedPrefix);
        UnderCurrentKeyOnly(store.Recipients[0].Destination[RecipientSecretProtector.EncryptedPrefix.Length..]).Should().Be("https://hooks.example.test/a");
        UnderCurrentKeyOnly(store.Recipients[0].HeadersJson![RecipientSecretProtector.EncryptedPrefix.Length..]).Should().Be("{\"X\":\"1\"}");
    }

    [Test]
    public async Task AValueUnderTheCurrentKey_IsLeftAsStored()
    {
        var current = _encryption.Encrypt("Host=current");
        var store = new Store();
        store.DataSources.Add(DataSource(1, current));

        var result = await Service(store).ReEncryptAsync(CancellationToken.None);

        result.UnderPreviousKeys.Should().Be(0);
        store.DataSources[0].EncryptedConnectionData.Should().Be(current);
        _logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Information && x.Message.Contains("can be removed"));
    }

    [Test]
    public async Task AValueNoKeyDecrypts_IsCountedAndLoggedById_AndLeftAsStored()
    {
        var foreign = new EncryptionService("a key nobody configured").Encrypt("Host=foreign");
        var store = new Store();
        store.DataSources.Add(DataSource(9, foreign));

        var result = await Service(store).ReEncryptAsync(CancellationToken.None);

        result.Unreadable.Should().Be(1);
        store.DataSources[0].EncryptedConnectionData.Should().Be(foreign);
        _logs.Entries.Should().Contain(x => x.Message == "Stored value of data source 9 cannot be decrypted with the current key or any previous key");
        _logs.Entries.Should().NotContain(x => x.Message.Contains("Host=foreign"));
    }

    // On the Npgsql provider with every command captured: the reads take archived rows and only sensitive settings and
    // encrypted recipient secrets, and each write is conditional on the value read, so a value saved meanwhile (the
    // database answers that no row matched) is counted as changed and kept.
    [Test]
    public async Task EachWrite_IsConditionalOnTheValueRead_AndAValueChangedMeanwhileIsKept_Translates()
    {
        var old = _previous.Encrypt("Host=old");
        var database = new CommandCapture { Rows = [[1, old, null]], RowsAffected = 0 };
        var service = new StoredValueReEncryptionService(database.NpgsqlFactory(), _encryption, _logs.For<StoredValueReEncryptionService>());

        var result = await service.ReEncryptAsync(CancellationToken.None);

        result.Should().Be(new StoredValueReEncryptionResult(UnderPreviousKeys: 3, ReEncrypted: 0, SkippedAsChanged: 3, Unreadable: 0, Failed: 0));
        var reads = database.Commands
            .Where(x => x.Text.StartsWith("SELECT", StringComparison.Ordinal))
            .Select(x => x.Text)
            .ToList();
        reads.Should().HaveCount(4);
        reads[0].Should().Contain("FROM beacon.data_sources").And.NotContain("archived_time");
        reads[2].Should().Contain("a.is_sensitive").And.Contain("a.value IS NOT NULL");
        reads[3].Should().Contain("LIKE 'enc:%'").And.NotContain("archived_time");
        var writes = database.Commands
            .Where(x => x.Text.StartsWith("UPDATE", StringComparison.Ordinal))
            .ToList();
        writes.Should().HaveCount(3);
        writes[0].Text.Should().Contain("encrypted_connection_data = @").And.Contain("WHERE d.id = @").And.Contain("d.encrypted_connection_data = @");
        writes[0].Parameters.Should().Contain(old, "the write is conditional on the value read");
        writes[1].Text.Should().Contain("g.encrypted_access_token = @");
        writes[2].Text.Should().Contain("a.value = @");
    }

    [Test]
    public async Task Counting_ChangesNothing()
    {
        var old = _previous.Encrypt("Host=old");
        var store = new Store();
        store.DataSources.Add(DataSource(1, old));
        store.DataSources.Add(DataSource(2, new EncryptionService("a key nobody configured").Encrypt("x")));

        var count = await Service(store).CountAsync(CancellationToken.None);

        count.Should().Be(new StoredValueReEncryptionResult(1, 0, 0, 1, 0));
        store.DataSources[0].EncryptedConnectionData.Should().Be(old);
        _logs.Entries.Should().BeEmpty();
    }

    [Test]
    public async Task TheStartupWarning_ReportsTheCounts_WithoutAKey()
    {
        var store = new Store();
        store.DataSources.Add(DataSource(1, _previous.Encrypt("Host=old")));
        var services = new ServiceCollection()
            .AddScoped<IStoredValueReEncryptionService>(_ => Service(store))
            .BuildServiceProvider();
        var warnings = new EncryptionKeyStartupWarnings(Configuration(), _logs.For<EncryptionKeyStartupWarnings>());

        await warnings.WarnAboutValuesUnderPreviousKeysAsync(services, CancellationToken.None);

        var entry = _logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Subject;
        entry.Message.Should().StartWith("1 stored values are still encrypted with a key in Beacon:PreviousEncryptionKeys, and 0 with no configured key");
        entry.Message.Should().NotContain(PreviousKey);
    }

    [Test]
    public void APreviousKeyGivenAsASingleValue_IsWarnedAbout_WithoutTheValue()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:PreviousEncryptionKeys"] = PreviousKey })
            .Build();

        new EncryptionKeyStartupWarnings(configuration, _logs.For<EncryptionKeyStartupWarnings>()).Configure(_ => { });

        var entry = _logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Subject;
        entry.Message.Should().Contain("is a single value, not a list").And.Contain("Beacon__PreviousEncryptionKeys__0");
        entry.Message.Should().NotContain(PreviousKey);
    }

    [Test]
    public async Task AValueThatCannotBeWritten_IsCountedAndLoggedById_TheOthersAreStillWritten_AndTheRunFailsAfterwards()
    {
        var old = _previous.Encrypt("Host=old");
        var database = new CommandCapture { Rows = [[1, old, null]], FailingWrite = 1 };
        var service = new StoredValueReEncryptionService(database.NpgsqlFactory(), _encryption, _logs.For<StoredValueReEncryptionService>());

        var run = () => service.ReEncryptAsync(CancellationToken.None);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("1 stored values could not be re-encrypted*");
        database.Writes.Should().Be(3, "a failed write does not stop the run");
        _logs.Entries.Should().Contain(x => x.Level == LogLevel.Error && x.Message == "Stored value of data source 1 could not be re-encrypted (InvalidOperationException)");
        _logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning && x.Message.Contains("1 failed", StringComparison.Ordinal));
        _logs.Entries.Should().NotContain(x => x.Message.Contains("Host=old", StringComparison.Ordinal));
    }

    [Test]
    public async Task AHostsOwnEncryptionService_IsLeftAlone_AndTheRunSaysWhy()
    {
        var store = new Store();
        var service = new StoredValueReEncryptionService(store.Factory(), Mock.Of<IEncryptionService>(), _logs.For<StoredValueReEncryptionService>());

        var result = await service.ReEncryptAsync(CancellationToken.None);
        var counted = await service.CountAsync(CancellationToken.None);

        result.Should().Be(new StoredValueReEncryptionResult(0, 0, 0, 0, 0));
        counted.Should().Be(new StoredValueReEncryptionResult(0, 0, 0, 0, 0));
        _logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning && x.Message.Contains("the host replaced IEncryptionService", StringComparison.Ordinal));
    }

    [Test]
    public async Task TheSampleHostsJob_RunsTheReEncryption()
    {
        var reEncryption = new Mock<IStoredValueReEncryptionService>();

        await new Beacon.SampleProject.Warp.Jobs.ReEncryptStoredValuesJobHandler(reEncryption.Object)
            .HandleAsync(new Beacon.SampleProject.Warp.Jobs.ReEncryptStoredValuesJob(), CancellationToken.None);

        reEncryption.Verify(x => x.ReEncryptAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private string UnderCurrentKeyOnly(string cipherText)
    {
        _encryption.Identify(cipherText).Should().Be(EncryptionService.KeyMatch.Current);

        return new EncryptionService(CurrentKey).Decrypt(cipherText);
    }

    private StoredValueReEncryptionService Service(Store store)
    {
        return new StoredValueReEncryptionService(store.Factory(), _encryption, _logs.For<StoredValueReEncryptionService>());
    }

    private static IConfiguration Configuration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:PreviousEncryptionKeys:0"] = PreviousKey })
            .Build();
    }

    private static DataSource DataSource(int id, string encrypted, bool archived = false)
    {
        return new DataSource
        {
            Id = id,
            Name = $"source-{id}",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = encrypted,
            ArchivedTime = archived ? DateTime.UtcNow : null
        };
    }

    // The rows each set serves.
    private sealed class Store
    {
        public List<DataSource> DataSources { get; } = [];

        public List<GitHubRepository> Repositories { get; } = [];

        public List<AppSetting> Settings { get; } = [];

        public List<Recipient> Recipients { get; } = [];

        public IDbContextFactory<BeaconContext> Factory()
        {
            var saved = new List<object>();
            var sets = new Dictionary<Type, object>
            {
                [typeof(DataSource)] = RecordingBeaconContext.MemorySet(DataSources, saved),
                [typeof(GitHubRepository)] = RecordingBeaconContext.MemorySet(Repositories, saved),
                [typeof(AppSetting)] = RecordingBeaconContext.MemorySet(Settings, saved),
                [typeof(Recipient)] = RecordingBeaconContext.MemorySet(Recipients, saved)
            };
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));

            return factory.Object;
        }
    }
}
