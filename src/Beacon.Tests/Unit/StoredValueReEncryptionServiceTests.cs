using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Api;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Models.Settings;
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
/// logged by id; a keyed recipient hash that cannot be rewritten under the current key keeps the previous keys needed. The
/// count-only run changes nothing, and the startup warning reports its counts.
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

    // On the Npgsql provider with every command captured: the reads take archived rows and only sensitive settings,
    // encrypted recipient secrets and staged LLM connection changes, and each write is conditional on the value read, so a
    // value saved meanwhile (the database answers that no row matched) is counted as changed and kept.
    [Test]
    public async Task EachWrite_IsConditionalOnTheValueRead_AndAValueChangedMeanwhileIsKept_Translates()
    {
        var old = _previous.Encrypt("Host=old");
        var database = new CommandCapture { Rows = [[1, old, null]], RowsAffected = 0 };
        var service = new StoredValueReEncryptionService(database.NpgsqlFactory(), _encryption, _logs.For<StoredValueReEncryptionService>());

        var result = await service.ReEncryptAsync(CancellationToken.None);

        result.Should().Be(new StoredValueReEncryptionResult(UnderPreviousKeys: 4, ReEncrypted: 0, SkippedAsChanged: 4, Unreadable: 0, Failed: 0));
        var reads = database.Commands
            .Where(x => x.Text.StartsWith("SELECT", StringComparison.Ordinal))
            .Select(x => x.Text)
            .ToList();
        reads.Should().HaveCount(5);
        reads[0].Should().Contain("FROM beacon.data_sources").And.NotContain("archived_time");
        reads[2].Should().Contain("a.is_sensitive").And.Contain("a.value IS NOT NULL");
        reads[3].Should().Contain("LIKE 'enc:%'").And.NotContain("archived_time");
        reads[4].Should().Contain("FROM beacon.change_approval_requests").And.Contain("c.encrypted_payload IS NOT NULL");
        var writes = database.Commands
            .Where(x => x.Text.StartsWith("UPDATE", StringComparison.Ordinal))
            .ToList();
        writes.Should().HaveCount(4);
        writes[0].Text.Should().Contain("encrypted_connection_data = @").And.Contain("WHERE d.id = @").And.Contain("d.encrypted_connection_data = @");
        writes[0].Parameters.Should().Contain(old, "the write is conditional on the value read");
        writes[1].Text.Should().Contain("g.encrypted_access_token = @");
        writes[2].Text.Should().Contain("a.value = @");
        writes[3].Text.Should().Contain("encrypted_payload = @").And.Contain("c.encrypted_payload = @");
        writes[3].Parameters.Should().Contain(old);
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
        database.Writes.Should().Be(4, "a failed write does not stop the run");
        _logs.Entries.Should().Contain(x => x.Level == LogLevel.Error && x.Message == "Stored value of data source 1 could not be re-encrypted (InvalidOperationException)");
        _logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning && x.Message.Contains("1 failed", StringComparison.Ordinal));
        _logs.Entries.Should().NotContain(x => x.Message.Contains("Host=old", StringComparison.Ordinal));
    }

    // The keyed hashes of where governed recipients deliver move to the current key too: an approved hash, and a pending
    // request's, that match the definition the recipient holds only under the previous key are recomputed; an approved hash
    // of a definition the recipient no longer holds cannot be (its plaintext is not stored), keeps matching under the
    // previous key while that key is configured, and is counted as an unresolved key dependency.
    [Test]
    public async Task RecipientApprovalHashesUnderAPreviousKey_AreRecomputedWithTheCurrentKey()
    {
        const string hook = "https://hooks.example.test/a";
        const string moved = "https://hooks.example.test/b";
        var before = new RecipientDestinationHasher(PreviousKey);
        var hasher = new RecipientDestinationHasher(CurrentKey, [PreviousKey]);
        var store = new Store();
        store.Recipients.Add(Governed(7, hook, RecipientDestinationDefinition.Hash(before, NotificationType.Webhook, hook, null)));
        store.Recipients.Add(Governed(8, moved, RecipientDestinationDefinition.Hash(before, NotificationType.Webhook, hook, null)));
        store.Requests.Add(new ChangeApprovalRequest
        {
            Id = 30,
            Kind = ChangeApprovalKind.RecipientDestination,
            ResourceId = 8,
            Status = ApprovalStatus.Pending,
            DefinitionHash = RecipientDestinationDefinition.Hash(before, NotificationType.Webhook, moved, null)
        });

        var counted = await Service(store, hasher).CountAsync(CancellationToken.None);
        var result = await Service(store, hasher).ReEncryptAsync(CancellationToken.None);
        var again = await Service(store, hasher).CountAsync(CancellationToken.None);

        counted.UnderPreviousKeys.Should().Be(2);
        result.Should().Be(new StoredValueReEncryptionResult(2, 2, 0, 0, 0) { UnresolvedKeyDependencies = 1 });
        store.Recipients[0].ApprovedDestinationHash.Should().Be(RecipientDestinationDefinition.Hash(hasher, NotificationType.Webhook, hook, null));
        store.Recipients[1].ApprovedDestinationHash.Should().Be(RecipientDestinationDefinition.Hash(before, NotificationType.Webhook, hook, null), "the approved definition is not the one it holds");
        store.Requests[0].DefinitionHash.Should().Be(RecipientDestinationDefinition.Hash(hasher, NotificationType.Webhook, moved, null));
        again.UnderPreviousKeys.Should().Be(0);
        again.UnresolvedKeyDependencies.Should().Be(1);
    }

    // An approved hash of a destination the recipient has moved away from (it holds a pending change) cannot be rewritten
    // under the current key, and nothing tells which key wrote it: the run counts it as an unresolved key dependency, logs
    // its id, and neither it nor the startup check reports the previous keys removable while it remains. Once the pending
    // change is approved, the approved hash is the current key's and the run reports them removable.
    [Test]
    public async Task AnApprovedHashOfADestinationTheRecipientNoLongerHolds_KeepsThePreviousKeysNeeded_UntilThePendingChangeIsDecided()
    {
        const string hook = "https://hooks.example.test/a";
        const string moved = "https://hooks.example.test/b";
        var before = new RecipientDestinationHasher(PreviousKey);
        var hasher = new RecipientDestinationHasher(CurrentKey, [PreviousKey]);
        var store = new Store();
        var recipient = Governed(8, moved, RecipientDestinationDefinition.Hash(before, NotificationType.Webhook, hook, null));
        store.Recipients.Add(recipient);
        var request = PendingChange(30, recipient, RecipientDestinationDefinition.Hash(before, NotificationType.Webhook, moved, null), hasher);
        store.Requests.Add(request);

        var counted = await Service(store, hasher).CountAsync(CancellationToken.None);
        var withoutPreviousKey = await Service(store, new RecipientDestinationHasher(CurrentKey)).CountAsync(CancellationToken.None);
        var result = await Service(store, hasher).ReEncryptAsync(CancellationToken.None);
        await StartupWarnings().WarnAboutValuesUnderPreviousKeysAsync(Services(store, hasher), CancellationToken.None);

        counted.Should().BeEquivalentTo(new { UnderPreviousKeys = 1, UnresolvedKeyDependencies = 1, PreviousKeysRemovable = false });
        withoutPreviousKey.UnresolvedKeyDependencies.Should().Be(0, "with no previous key configured, nothing depends on one");
        result.Should().BeEquivalentTo(new { UnderPreviousKeys = 1, ReEncrypted = 1, Unreadable = 0, Failed = 0, UnresolvedKeyDependencies = 1, PreviousKeysRemovable = false });
        request.DefinitionHash.Should().Be(RecipientDestinationDefinition.Hash(hasher, NotificationType.Webhook, moved, null), "the request names the destination the recipient holds");
        _logs.Entries.Should().Contain(x => x.Level == LogLevel.Warning && x.Message == "The keyed hash of recipient approval 8 names a definition recipient 8 does not hold, so it cannot be rewritten under the current key and may still need a previous key");
        _logs.Entries.Should().Contain(x => x.Level == LogLevel.Warning && x.Message.StartsWith("0 stored values are still encrypted with a key in Beacon:PreviousEncryptionKeys, and 0 with no configured key; 1 keyed hashes name a definition their recipient or data source does not hold", StringComparison.Ordinal));
        _logs.Entries.Should().NotContain(x => x.Message.Contains("can be removed", StringComparison.Ordinal));
        _logs.Entries.Should().NotContain(x => x.Message.Contains("hooks.example.test", StringComparison.Ordinal));

        await Approvals(store, hasher).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);
        _logs = new LogRecorder();
        var decided = await Service(store, hasher).ReEncryptAsync(CancellationToken.None);

        recipient.ApprovedDestinationHash.Should().Be(RecipientDestinationDefinition.Hash(hasher, NotificationType.Webhook, moved, null));
        decided.Should().Be(new StoredValueReEncryptionResult(0, 0, 0, 0, 0));
        decided.PreviousKeysRemovable.Should().BeTrue();
        _logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Information && x.Message.Contains("can be removed", StringComparison.Ordinal));
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

    // A staged LLM connection change is encrypted at rest like any other value: one under a previous key is re-encrypted.
    [Test]
    public async Task AStagedLlmConnectionChangeUnderAPreviousKey_IsReEncryptedWithTheCurrentKey()
    {
        var store = new Store();
        var request = LlmChange(80, _previous.Encrypt("""{"provider":1}"""), "v1:based-on");
        store.Requests.Add(request);

        var result = await Service(store).ReEncryptAsync(CancellationToken.None);

        result.Should().BeEquivalentTo(new { UnderPreviousKeys = 1, ReEncrypted = 1, Unreadable = 0, Failed = 0 });
        UnderCurrentKeyOnly(request.EncryptedPayload!).Should().Be("""{"provider":1}""");
    }

    // A governed data source's approved hash, and its pending request's, that match its definition only under the previous
    // key are recomputed under the current key from its stored connection; an approved hash naming a definition the source
    // no longer holds (its change waits) cannot be, and keeps the previous key needed.
    [Test]
    public async Task DataSourceDefinitionHashesUnderAPreviousKey_AreRecomputedWithTheCurrentKey()
    {
        const string connection = "Host=db.example.com;Database=sales;Username=reader;Password=pw;SSL Mode=VerifyFull";
        const string moved = "Host=db.example.com;Database=ledger;Username=reader;Password=pw;SSL Mode=VerifyFull";
        var before = new ConnectionDefinitionHasher(PreviousKey);
        var hasher = new ConnectionDefinitionHasher(CurrentKey, [PreviousKey]);
        var store = new Store();
        var approved = Governed(store, 1, connection, DefinitionHash(before, connection));
        var waiting = Governed(store, 2, moved, DefinitionHash(before, connection));
        var request = LlmChange(81, null, DefinitionHash(before, moved));
        request.Kind = ChangeApprovalKind.DataSourceConnection;
        request.ResourceId = waiting.Id;
        store.Requests.Add(request);

        var counted = await Service(store, hasher).CountAsync(CancellationToken.None);
        var result = await Service(store, hasher).ReEncryptAsync(CancellationToken.None);

        counted.Should().BeEquivalentTo(new { UnderPreviousKeys = 2, UnresolvedKeyDependencies = 1, PreviousKeysRemovable = false });
        result.Should().BeEquivalentTo(new { UnderPreviousKeys = 2, ReEncrypted = 2, Failed = 0, UnresolvedKeyDependencies = 1, PreviousKeysRemovable = false });
        approved.ApprovedTargetHash.Should().Be(DefinitionHash(hasher, connection));
        request.DefinitionHash.Should().Be(DefinitionHash(hasher, moved));
        waiting.ApprovedTargetHash.Should().Be(DefinitionHash(before, connection), "its plaintext is not stored");
        _logs.Entries.Should().Contain(x => x.Level == LogLevel.Warning && x.Message == "The keyed hash of data source approval 2 cannot be rewritten under the current key and may still need a previous key");
        _logs.Entries.Should().NotContain(x => x.Message.Contains("db.example.com", StringComparison.Ordinal) || x.Message.Contains("ledger", StringComparison.Ordinal));
    }

    // A pending LLM connection change made under the previous key (the stored connection's hash it records, or the
    // fingerprints it shows) cannot be rewritten (its reviewer's view is bound by its payload hash): it keeps the previous
    // key needed until it is decided or expires. A run wipes one past its expiry; one made under the current key needs no
    // previous key.
    [Test]
    public async Task APendingLlmConnectionChangeUnderAPreviousKey_KeepsThePreviousKeysNeeded_AndARunExpiresTheOldOnes()
    {
        var before = new ConnectionDefinitionHasher(PreviousKey);
        var hasher = new ConnectionDefinitionHasher(CurrentKey, [PreviousKey]);
        var store = new Store();
        store.Settings.Add(new AppSetting { Id = 1, Key = "LLM.Provider", Category = "LLM", Value = nameof(AiProvider.OpenAI) });
        var stored = LlmConnectionDefinition.Of(new AppSettingsData { LlmProvider = AiProvider.OpenAI });
        var proposed = new LlmConnectionDefinition { Provider = AiProvider.Claude, ApiKey = "proposed-key" };
        var payload = _encryption.Encrypt(JsonSerializer.Serialize(proposed, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var basedOnBefore = LlmChange(82, payload, stored.Hash(before), proposed.View(hasher, null));
        var shownBefore = LlmChange(84, payload, stored.Hash(hasher), proposed.View(before, null));
        var current = LlmChange(85, payload, stored.Hash(hasher), proposed.View(hasher, null));
        var expired = LlmChange(83, payload, stored.Hash(before), proposed.View(before, null));
        expired.CreatedTime = DateTime.UtcNow.AddDays(-31);
        store.Requests.AddRange([basedOnBefore, shownBefore, current, expired]);
        var llm = TestChangeApprovals.Llm(store.Factory(), TestChangeApprovals.On(), hasher: hasher, encryption: _encryption);

        var counted = await Service(store, hasher, llm).CountAsync(CancellationToken.None);
        var expiredAfterCount = expired.Status;
        var result = await Service(store, hasher, llm).ReEncryptAsync(CancellationToken.None);

        counted.UnresolvedKeyDependencies.Should().Be(3, "counting changes nothing, so the old request is still pending");
        expiredAfterCount.Should().Be(ApprovalStatus.Pending);
        result.Should().BeEquivalentTo(new { UnresolvedKeyDependencies = 2, PreviousKeysRemovable = false });
        expired.Should().BeEquivalentTo(new { Status = ApprovalStatus.Superseded, EncryptedPayload = (string?)null }, x => x.ExcludingMissingMembers());
        _logs.Entries.Should().Contain(x => x.Level == LogLevel.Warning && x.Message == "The keyed hash of LLM connection change request 82 cannot be rewritten under the current key and may still need a previous key");
        _logs.Entries.Should().Contain(x => x.Message.EndsWith("request 84 cannot be rewritten under the current key and may still need a previous key", StringComparison.Ordinal));
        _logs.Entries.Should().NotContain(x => x.Message.Contains("request 85", StringComparison.Ordinal));

        var withoutPreviousKey = await Service(store, new ConnectionDefinitionHasher(CurrentKey), TestChangeApprovals.Llm(store.Factory(), hasher: new ConnectionDefinitionHasher(CurrentKey), encryption: _encryption))
            .CountAsync(CancellationToken.None);
        withoutPreviousKey.UnresolvedKeyDependencies.Should().Be(0);
    }

    private string UnderCurrentKeyOnly(string cipherText)
    {
        _encryption.Identify(cipherText).Should().Be(EncryptionService.KeyMatch.Current);

        return new EncryptionService(CurrentKey).Decrypt(cipherText);
    }

    private StoredValueReEncryptionService Service(Store store, RecipientDestinationHasher? hasher = null)
    {
        return new StoredValueReEncryptionService(store.Factory(), _encryption, _logs.For<StoredValueReEncryptionService>(), hasher);
    }

    // With the data-source definition hashes (read through the connection resolver under connectionHasher) and the LLM
    // connection changes in the run.
    private StoredValueReEncryptionService Service(Store store, ConnectionDefinitionHasher connectionHasher, LlmConnectionApproval? llm = null)
    {
        var resolver = new DataSourceConnectionResolver(_encryption, new ConfigurationBuilder().Build(), TestTransport.Policy(), new BeaconConfiguration(), connectionHasher);

        return new StoredValueReEncryptionService(store.Factory(), _encryption, _logs.For<StoredValueReEncryptionService>(), null, resolver, connectionHasher, llm);
    }

    private static string DefinitionHash(ConnectionDefinitionHasher hasher, string connection)
    {
        return hasher.Hash(
            ConnectionDefinitionPurpose.DataSourceDefinition,
            DataSourceApprovalDefinition.Canonical(TestTransport.Policy(), DataSourceType.Database, DatabaseEngineType.PostgreSQL, connection));
    }

    // A governed PostgreSQL source whose connection is encrypted with the current key.
    private DataSource Governed(Store store, int id, string connection, string approvedHash)
    {
        var source = new DataSource
        {
            Id = id,
            Name = $"source-{id}",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = _encryption.Encrypt(connection),
            ApprovedTargetHash = approvedHash
        };
        store.DataSources.Add(source);

        return source;
    }

    // A pending change of the LLM connection, its staged payload and recorded hash as given.
    private static ChangeApprovalRequest LlmChange(int id, string? payload, string definitionHash, Beacon.Core.Models.Approvals.LlmConnectionView? view = null)
    {
        var proposedJson = view == null ? "{}" : JsonSerializer.Serialize(view, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        return new ChangeApprovalRequest
        {
            Id = id,
            Kind = ChangeApprovalKind.LlmConnection,
            ResourceId = LlmConnectionApproval.ResourceId,
            ResourceName = LlmConnectionApproval.ResourceName,
            Status = ApprovalStatus.Pending,
            DefinitionHash = definitionHash,
            ProposedJson = proposedJson,
            PayloadHash = ChangeApprovalHashes.Sha256(proposedJson),
            RequestedByUserId = "ext-ada",
            EncryptedPayload = payload
        };
    }

    private ServiceProvider Services(Store store, RecipientDestinationHasher hasher)
    {
        return new ServiceCollection()
            .AddScoped<IStoredValueReEncryptionService>(_ => Service(store, hasher))
            .BuildServiceProvider();
    }

    private EncryptionKeyStartupWarnings StartupWarnings()
    {
        return new EncryptionKeyStartupWarnings(Configuration(), _logs.For<EncryptionKeyStartupWarnings>());
    }

    // Change requests decided by a reviewer other than the requester.
    private ChangeApprovalService Approvals(Store store, RecipientDestinationHasher hasher)
    {
        return new ChangeApprovalService(
            store.Factory(),
            TestChangeApprovals.Caller("ext-cid"),
            Mock.Of<IBeaconUserContext>(),
            new RecipientSecretProtector(_encryption),
            hasher,
            TestChangeApprovals.On(),
            new RecordingSecurityAudit(),
            TestPermissions.Admin,
            TestChangeApprovals.NoDefinitions,
            TestChangeApprovals.NoProbe,
            TestChangeApprovals.Llm(),
            NullLogger<ChangeApprovalService>.Instance);
    }

    // A pending change of where the recipient delivers to what it holds now, requested by someone other than the reviewer,
    // with the view a reviewer approves.
    private ChangeApprovalRequest PendingChange(int id, Recipient recipient, string definitionHash, RecipientDestinationHasher hasher)
    {
        var view = RecipientDestinationDefinition.ViewOfStored(new RecipientSecretProtector(_encryption), hasher, recipient.NotificationType, recipient.Destination, recipient.HeadersJson);
        var proposedJson = JsonSerializer.Serialize(view, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        return new ChangeApprovalRequest
        {
            Id = id,
            Kind = ChangeApprovalKind.RecipientDestination,
            ResourceId = recipient.Id,
            ResourceName = recipient.Name,
            Status = ApprovalStatus.Pending,
            DefinitionHash = definitionHash,
            ProposedJson = proposedJson,
            PayloadHash = ChangeApprovalHashes.Sha256(proposedJson),
            RequestedByUserId = "ext-ada"
        };
    }

    // A governed webhook recipient whose destination is encrypted with the current key.
    private Recipient Governed(int id, string destination, string approvedHash)
    {
        return new Recipient
        {
            Id = id,
            Name = $"hook-{id}",
            NotificationType = NotificationType.Webhook,
            Destination = RecipientSecretProtector.EncryptedPrefix + _encryption.Encrypt(destination),
            ApprovedDestinationHash = approvedHash
        };
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

        public List<ChangeApprovalRequest> Requests { get; } = [];

        public List<BeaconUser> Users { get; } = [];

        // Transactions (a decision on a change request opens one) are stand-ins; nothing connects.
        public SqlCapture Offline { get; } = new();

        public IDbContextFactory<BeaconContext> Factory()
        {
            var saved = new List<object>();
            var sets = new Dictionary<Type, object>
            {
                [typeof(DataSource)] = RecordingBeaconContext.MemorySet(DataSources, saved),
                [typeof(GitHubRepository)] = RecordingBeaconContext.MemorySet(Repositories, saved),
                [typeof(AppSetting)] = RecordingBeaconContext.MemorySet(Settings, saved),
                [typeof(Recipient)] = RecordingBeaconContext.MemorySet(Recipients, saved),
                [typeof(ChangeApprovalRequest)] = RecordingBeaconContext.MemorySet(Requests, saved),
                [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(Users, saved)
            };
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(sets, saved, Offline));

            return factory.Object;
        }
    }
}
