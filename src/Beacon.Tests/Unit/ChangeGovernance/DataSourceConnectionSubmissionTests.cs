using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Models.DataSources;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// Saving a data source's connection under the change approval workflow (connections covered): a created source is saved
/// held with a pending request, in one commit, and connects to nothing; a change of any setting but the secrets is saved,
/// submitted and holds the source (no engine probe, no metadata re-scan); a change of the secrets alone is an immediate
/// rotation; a source not governed yet becomes governed under its old definition; a stored connection that cannot be read
/// counts as changed; another person's waiting change refuses the save (409) and the requester's own is replaced. With the
/// switch off a definition change makes the source ungoverned and supersedes what waited; deleting a source supersedes its
/// pending requests. The approval's probe refuses a declared engine the server is not (409).
/// </summary>
[TestFixture]
public class DataSourceConnectionSubmissionTests
{
    private const string Ada = "ext-ada";
    private const string Ben = "ext-ben";
    private const string Connection = "Host=db.example.com;Database=sales;Username=reader;Password=pw-one;SSL Mode=VerifyFull";
    private const string Rotated = "Host=db.example.com;Database=sales;Username=reader;Password=pw-two;SSL Mode=VerifyFull";
    private const string OtherDatabase = "Host=db.example.com;Database=ledger;Username=reader;Password=pw-one;SSL Mode=VerifyFull";
    private const string OtherLogin = "Host=db.example.com;Database=sales;Username=writer;Password=pw-one;SSL Mode=VerifyFull";

    private static readonly EncryptionService Encryption = new(TestEncryptionKey.Value);

    [Test]
    public async Task Create_UnderCoverage_SavesTheSourceHeld_WithAPendingRequest_InOneCommit()
    {
        var store = new ChangeApprovalStore();
        var verifier = TestEngineVerifier.Passing;
        var audit = new RecordingSecurityAudit();

        var response = await Service(store, TestChangeApprovals.CoveringConnections(), verifier: verifier, audit: audit)
            .CreateDataSource(Data(Connection), CancellationToken.None);
        store.Commit();

        response.Success.Should().BeTrue();
        response.Message.Should().Be(DataSourceService.SubmittedForApprovalMessage);
        var created = store.Saved.OfType<DataSource>().Distinct().Should().ContainSingle().Subject;
        created.ApprovedTargetHash.Should().Be(DataSourceApprovalDefinition.Held);
        var request = store.Requests.Should().ContainSingle().Subject;
        request.Should().BeEquivalentTo(
            new
            {
                Kind = ChangeApprovalKind.DataSourceConnection,
                ResourceId = created.Id,
                Status = ApprovalStatus.Pending,
                DefinitionHash = HashOf(Connection),
                RequestedByUserId = Ada,
                ChangeSummary = "Data source created"
            },
            x => x.ExcludingMissingMembers());
        request.ProposedJson.Should().NotContain("pw-one");
        verifier.Calls.Should().Be(0, "a held source connects to nothing: its engine is checked at approval");
        store.Offline.Commits.Should().Be(1, "the source, its request and their events commit together");
        store.SaveCount.Should().Be(2, "the source's save gives it the id its request names");
        audit.Events.Select(x => x.Action).Should().Equal(SecurityAuditActions.DataSourceCreated, SecurityAuditActions.ChangeSubmitted);
        var connect = () => Resolver().GetConnectionString(created);
        connect.Should().Throw<DataSourceAwaitingApprovalException>();
    }

    [Test]
    public async Task Create_UnderCoverage_CommitsNothing_WhenItsEventsCannotBeSaved()
    {
        var store = new ChangeApprovalStore();
        var audit = new Mock<ISecurityAuditRecorder>();
        audit
            .Setup(x => x.SaveWithEventsAsync(It.IsAny<BeaconContext>(), It.IsAny<Func<IEnumerable<SecurityAuditEntry>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("save failed"));

        var create = () => Service(store, TestChangeApprovals.CoveringConnections(), audit: audit.Object)
            .CreateDataSource(Data(Connection), CancellationToken.None);

        await create.Should().ThrowAsync<DbUpdateException>();
        store.Offline.TransactionIsolationLevels.Should().ContainSingle("the source and its request are saved in one transaction");
        store.SaveCount.Should().Be(1, "the source was saved inside the transaction");
        store.Offline.Commits.Should().Be(0, "the source is rolled back with its request");
    }

    [Test]
    public async Task Create_WithCoverageOff_ProbesTheEngine_AndRecordsNoHold()
    {
        var store = new ChangeApprovalStore();
        var verifier = TestEngineVerifier.Passing;

        var response = await Service(store, TestChangeApprovals.On(), verifier: verifier).CreateDataSource(Data(Connection), CancellationToken.None);

        response.Message.Should().BeNull();
        store.Saved.OfType<DataSource>().Distinct().Should().ContainSingle().Which.ApprovedTargetHash.Should().BeNull();
        store.Saved.OfType<ChangeApprovalRequest>().Should().BeEmpty();
        verifier.Calls.Should().Be(1);
        store.Offline.TransactionIsolationLevels.Should().BeEmpty();
    }

    [Test]
    public async Task ACallerBeaconCannotName_IsRefused_AndNothingIsWritten()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, HashOf(Connection));
        var service = Service(store, TestChangeApprovals.CoveringConnections(), TestChangeApprovals.Caller(null));

        var create = () => service.CreateDataSource(Data(Connection), CancellationToken.None);
        var update = () => service.UpdateDataSource(Data(OtherDatabase, source), CancellationToken.None);

        await create.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(ChangeApprovalSubmitter.UnidentifiedRequesterMessage);
        await update.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(ChangeApprovalSubmitter.UnidentifiedRequesterMessage);
        store.Saved.Should().BeEmpty();
        store.Offline.TransactionIsolationLevels.Should().BeEmpty();
        Encryption.Decrypt(source.EncryptedConnectionData).Should().Be(Connection);
    }

    // The requester withdraws a held change by saving the approved connection back: their request is superseded (and they
    // are told), none is added, and the source connects again; another person's save of it is refused while it waits.
    [Test]
    public async Task Update_ThatSavesTheApprovedConnectionBack_WithdrawsTheRequestersHeldChange()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, OtherDatabase, HashOf(Connection));
        var pending = store.AddRequest(ChangeApprovalKind.DataSourceConnection, source.Id, HashOf(OtherDatabase), Ben);
        var notifier = new Mock<IApprovalNotifier>();

        var byAda = () => Service(store, TestChangeApprovals.CoveringConnections(), TestChangeApprovals.Caller(Ada))
            .UpdateDataSource(Data(Connection, source, confirmTargetChange: true), CancellationToken.None);
        await byAda.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalSubmitter.OtherPendingMessage(ChangeApprovalKind.DataSourceConnection));
        pending.Status.Should().Be(ApprovalStatus.Pending);

        await Service(store, TestChangeApprovals.CoveringConnections(), TestChangeApprovals.Caller(Ben), notifier: notifier.Object)
            .UpdateDataSource(Data(Connection, source, confirmTargetChange: true), CancellationToken.None);
        store.Commit();

        pending.Status.Should().Be(ApprovalStatus.Superseded);
        store.Requests.Should().ContainSingle("no request is added for the approved connection");
        source.ApprovedTargetHash.Should().Be(HashOf(Connection));
        Resolver().GetConnectionString(source).Should().Be(Connection, "the source connects again");
        notifier.Verify(x => x.ApprovalUpdatedAsync(pending.Id, It.IsAny<string>(), null, Ben, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Only a change that waits for approval needs a requester Beacon can name; a rotation or a rename applies at once.
    [Test]
    public async Task ACallerBeaconCannotName_MakesTheChangesThatApplyAtOnce()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, HashOf(Connection));
        var data = Data(Rotated, source);
        data.Name = "sales-renamed";

        await Service(store, TestChangeApprovals.CoveringConnections(), TestChangeApprovals.Caller(null)).UpdateDataSource(data, CancellationToken.None);

        source.Name.Should().Be("sales-renamed");
        Encryption.Decrypt(source.EncryptedConnectionData).Should().Be(Rotated);
        source.ApprovedTargetHash.Should().Be(HashOf(Connection));
        store.Saved.OfType<ChangeApprovalRequest>().Should().BeEmpty();
    }

    // The engine check talks to the server: it runs before the unit of work takes the source's row, so no lock or open
    // transaction waits on it.
    [Test]
    public async Task Update_OfTheSecretsAlone_ChecksTheServer_BeforeTakingTheSourcesRow()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, HashOf(Connection));
        var verifier = new TransactionAwareVerifier(store);

        await Service(store, TestChangeApprovals.CoveringConnections(), verifier: verifier).UpdateDataSource(Data(Rotated, source), CancellationToken.None);

        verifier.TransactionsOpenedWhenProbed.Should().Equal(0);
        store.Offline.TransactionIsolationLevels.Should().ContainSingle();
        store.Offline.Commits.Should().Be(1);
    }

    // With the switch off a governed source's definition change supersedes what waited in the save's own transaction: a
    // save that fails leaves the requests pending.
    [Test]
    public async Task Update_WithCoverageOff_SupersedesWhatWaited_InTheSavesTransaction()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, OtherDatabase, HashOf(Connection));
        store.AddRequest(ChangeApprovalKind.DataSourceConnection, source.Id, HashOf(OtherDatabase), Ben);
        var audit = new Mock<ISecurityAuditRecorder>();
        audit
            .Setup(x => x.StageAsync(It.IsAny<BeaconContext>(), It.IsAny<SecurityAuditEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("save failed"));

        var update = () => Service(store, TestChangeApprovals.On(), audit: audit.Object)
            .UpdateDataSource(Data(OtherLogin, source, confirmTargetChange: true), CancellationToken.None);

        await update.Should().ThrowAsync<DbUpdateException>();
        store.Offline.TransactionIsolationLevels.Should().ContainSingle("the supersede and the save are one unit of work");
        store.Offline.Commits.Should().Be(0);
    }

    [Test]
    public async Task Update_OfTheSecretsAlone_IsAnImmediateRotation_WithNoRequest()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, HashOf(Connection));
        var verifier = TestEngineVerifier.Passing;

        await Service(store, TestChangeApprovals.CoveringConnections(), verifier: verifier).UpdateDataSource(Data(Rotated, source), CancellationToken.None);
        store.Commit();

        store.Requests.Should().BeEmpty();
        source.ApprovedTargetHash.Should().Be(HashOf(Connection)).And.Be(HashOf(Rotated), "the secrets are not part of the definition");
        verifier.Calls.Should().Be(1, "a rotation is checked against the server as before");
        Resolver().GetConnectionString(source).Should().Be(Rotated);
    }

    [Test]
    public async Task Update_OfANonSecretSetting_SubmitsAndHoldsTheSource_WithNoProbeOrReScan()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, HashOf(Connection));
        var verifier = TestEngineVerifier.Passing;
        var scheduler = new Mock<IBeaconScheduler>();

        await Service(store, TestChangeApprovals.CoveringConnections(), verifier: verifier, scheduler: scheduler.Object)
            .UpdateDataSource(Data(OtherDatabase, source, confirmTargetChange: true), CancellationToken.None);
        store.Commit();

        source.ApprovedTargetHash.Should().Be(HashOf(Connection), "the approved definition stays approved until the change is");
        var request = store.Requests.Should().ContainSingle().Subject;
        request.Should().BeEquivalentTo(
            new { ResourceId = source.Id, Status = ApprovalStatus.Pending, DefinitionHash = HashOf(OtherDatabase), RequestedByUserId = Ada, ChangeSummary = "Database changed" },
            x => x.ExcludingMissingMembers());
        request.ProposedJson.Should().NotContain("pw-one");
        var proposed = System.Text.Json.JsonSerializer.Deserialize<DataSourceConnectionView>(request.ProposedJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        proposed!.IsTargetChange.Should().BeTrue("the change was confirmed as a change of target");
        proposed.ChangedSettings.Should().Equal(["Database"], "the settings that changed are named, never their values");
        verifier.Calls.Should().Be(0);
        scheduler.Verify(x => x.EnqueueMetadataRefresh(It.IsAny<int>()), Times.Never);
        store.Offline.Commits.Should().Be(1);
        var connect = () => Resolver().GetConnectionString(source);
        connect.Should().Throw<DataSourceAwaitingApprovalException>();
    }

    [Test]
    public async Task Update_OfAnUngovernedSource_GovernsItUnderItsOldDefinition()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, approvedHash: null);

        await Service(store, TestChangeApprovals.CoveringConnections()).UpdateDataSource(Data(OtherLogin, source), CancellationToken.None);
        store.Commit();

        source.ApprovedTargetHash.Should().Be(HashOf(Connection));
        store.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { DefinitionHash = HashOf(OtherLogin), ChangeSummary = "Login, Username changed" },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task Update_OfAnUngovernedSource_ThatKeepsItsDefinition_SubmitsNothing()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, approvedHash: null);

        await Service(store, TestChangeApprovals.CoveringConnections()).UpdateDataSource(Data(Rotated, source), CancellationToken.None);

        source.ApprovedTargetHash.Should().BeNull();
        store.Saved.OfType<ChangeApprovalRequest>().Should().BeEmpty();
    }

    [Test]
    public async Task Update_WhenTheStoredConnectionCannotBeRead_IsGoverned()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, approvedHash: null);
        source.EncryptedConnectionData = new EncryptionService(Convert.ToBase64String(Enumerable.Range(0, 32).Select(x => (byte)(255 - (x * 7))).ToArray()))
            .Encrypt(Connection);
        var verifier = TestEngineVerifier.Passing;

        await Service(store, TestChangeApprovals.CoveringConnections(), verifier: verifier)
            .UpdateDataSource(Data(Connection, source, confirmTargetChange: true), CancellationToken.None);
        store.Commit();

        source.ApprovedTargetHash.Should().Be(DataSourceApprovalDefinition.Held, "nothing it held before can be read, so nothing is approved");
        store.Requests.Should().ContainSingle().Which.DefinitionHash.Should().Be(HashOf(Connection));
        verifier.Calls.Should().Be(0);
    }

    [Test]
    public async Task Update_WhileAnotherPersonsChangeWaits_Is409_AndCommitsNothing()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, OtherDatabase, HashOf(Connection));
        var pending = store.AddRequest(ChangeApprovalKind.DataSourceConnection, source.Id, HashOf(OtherDatabase), Ben);

        var update = () => Service(store, TestChangeApprovals.CoveringConnections())
            .UpdateDataSource(Data(OtherLogin, source, confirmTargetChange: true), CancellationToken.None);

        await update.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalSubmitter.OtherPendingMessage(ChangeApprovalKind.DataSourceConnection));
        pending.Status.Should().Be(ApprovalStatus.Pending);
        store.Saved.OfType<ChangeApprovalRequest>().Should().BeEmpty();
        store.SaveCount.Should().Be(0);
        store.Offline.Commits.Should().Be(0);
    }

    [Test]
    public async Task Update_ReplacesTheRequestersOwnWaitingChange_AndTellsThemAfterTheCommit()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, OtherDatabase, HashOf(Connection));
        var own = store.AddRequest(ChangeApprovalKind.DataSourceConnection, source.Id, HashOf(OtherDatabase), Ada);
        var notifier = new Mock<IApprovalNotifier>();

        await Service(store, TestChangeApprovals.CoveringConnections(), notifier: notifier.Object)
            .UpdateDataSource(Data(OtherLogin.Replace("Database=sales", "Database=ledger"), source), CancellationToken.None);
        store.Commit();

        own.Status.Should().Be(ApprovalStatus.Superseded);
        store.Requests.Where(x => x.Status == ApprovalStatus.Pending).Should().ContainSingle()
            .Which.DefinitionHash.Should().Be(HashOf(OtherLogin.Replace("Database=sales", "Database=ledger")));
        source.ApprovedTargetHash.Should().Be(HashOf(Connection));
        notifier.Verify(x => x.ApprovalUpdatedAsync(own.Id, It.IsAny<string>(), null, Ada, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Update_OfAHeldSource_ThatKeepsWhatItsRequestProposes_SubmitsNothingNew()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, DataSourceApprovalDefinition.Held);
        var pending = store.AddRequest(ChangeApprovalKind.DataSourceConnection, source.Id, HashOf(Connection), Ben);
        var verifier = TestEngineVerifier.Passing;

        await Service(store, TestChangeApprovals.CoveringConnections(), verifier: verifier).UpdateDataSource(Data(Rotated, source), CancellationToken.None);

        pending.Status.Should().Be(ApprovalStatus.Pending, "a held source's rotation leaves its proposed definition as it is");
        store.Saved.OfType<ChangeApprovalRequest>().Should().BeEmpty();
        source.ApprovedTargetHash.Should().Be(DataSourceApprovalDefinition.Held);
        verifier.Calls.Should().Be(0);
    }

    [Test]
    public async Task Update_OfASourceItsConnectorCannotRead_GovernsEvenARotation()
    {
        var store = new ChangeApprovalStore();
        var stored = """{"ProjectId":"analytics","ServiceAccountJson":"{\"type\":\"service_account\",\"private_key\":\"one\"}"}""";
        var rotated = """{"ProjectId":"analytics","ServiceAccountJson":"{\"type\":\"service_account\",\"private_key\":\"two\"}"}""";
        var source = Stored(store, stored, approvedHash: null, DataSourceType.BigQuery);

        await Service(store, TestChangeApprovals.CoveringConnections()).UpdateDataSource(Data(rotated, source, confirmTargetChange: true, type: DataSourceType.BigQuery), CancellationToken.None);
        store.Commit();

        source.ApprovedTargetHash.Should().Be(HashOf(stored, DataSourceType.BigQuery));
        var request = store.Requests.Should().ContainSingle().Subject;
        request.DefinitionHash.Should().Be(HashOf(rotated, DataSourceType.BigQuery));
        request.ChangeSummary.Should().Be("Connection changed (compare it by its fingerprint)");
        request.ProposedJson.Should().Contain("\"secretOnlyDefinition\":true").And.NotContain("private_key");
    }

    [Test]
    public async Task Update_WithCoverageOff_MakesAGovernedSourceUngoverned_AndSupersedesWhatWaited()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, OtherDatabase, HashOf(Connection));
        var pending = store.AddRequest(ChangeApprovalKind.DataSourceConnection, source.Id, HashOf(OtherDatabase), Ben);

        await Service(store, TestChangeApprovals.On()).UpdateDataSource(Data(OtherLogin, source, confirmTargetChange: true), CancellationToken.None);

        source.ApprovedTargetHash.Should().BeNull();
        pending.Status.Should().Be(ApprovalStatus.Superseded);
        store.Saved.OfType<ChangeApprovalRequest>().Should().BeEmpty();
    }

    [Test]
    public async Task Update_WithCoverageOff_ThatKeepsTheDefinition_KeepsTheApprovedHash()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, HashOf(Connection));
        var pending = store.AddRequest(ChangeApprovalKind.DataSourceConnection, source.Id, HashOf(OtherDatabase), Ben);

        await Service(store, new BeaconConfiguration()).UpdateDataSource(Data(Rotated, source), CancellationToken.None);

        source.ApprovedTargetHash.Should().Be(HashOf(Connection));
        pending.Status.Should().Be(ApprovalStatus.Pending);
    }

    [Test]
    public async Task Delete_SupersedesThePendingRequests_InTheSameCommit_AndTellsTheRequesters()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, OtherDatabase, HashOf(Connection));
        var pending = store.AddRequest(ChangeApprovalKind.DataSourceConnection, source.Id, HashOf(OtherDatabase), Ben);
        var decided = store.AddRequest(ChangeApprovalKind.DataSourceConnection, source.Id, HashOf(Connection), Ben, ApprovalStatus.Approved);
        var notifier = new Mock<IApprovalNotifier>();
        var audit = new RecordingSecurityAudit();

        await Service(store, TestChangeApprovals.CoveringConnections(), audit: audit, notifier: notifier.Object).DeleteDataSource(source.Id, CancellationToken.None);

        source.ArchivedTime.Should().NotBeNull();
        pending.Status.Should().Be(ApprovalStatus.Superseded);
        decided.Status.Should().Be(ApprovalStatus.Approved);
        store.Offline.Commits.Should().Be(1);
        audit.Single().DetailsJson.Should().Contain(pending.Id.ToString()).And.NotContain("db.example.com");
        notifier.Verify(x => x.ApprovalUpdatedAsync(pending.Id, It.IsAny<string>(), null, Ben, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Delete_OfAnUngovernedSource_OpensNoTransaction()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, approvedHash: null);

        await Service(store, TestChangeApprovals.CoveringConnections()).DeleteDataSource(source.Id, CancellationToken.None);

        source.ArchivedTime.Should().NotBeNull();
        store.Offline.TransactionIsolationLevels.Should().BeEmpty();
    }

    [Test]
    public async Task TheApprovalProbe_RefusesADeclaredEngineTheServerIsNot_With409()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, DataSourceApprovalDefinition.Held);
        var verifier = new MismatchedEngine();

        var probe = () => ((IHeldDataSourceProbe)Service(store, TestChangeApprovals.CoveringConnections(), verifier: verifier))
            .ProbeAsync(source.Id, CancellationToken.None);

        await probe.Should().ThrowAsync<BeaconConflictException>().WithMessage(MismatchedEngine.Message);
        verifier.Probed.Should().Equal((source.Id, Connection));
    }

    // A disabled source cannot be checked against its server, so its connection is not approved until it is enabled.
    [Test]
    public async Task TheApprovalProbe_ChecksTheStoredConnection_AndRefusesADisabledSource()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, DataSourceApprovalDefinition.Held);
        var verifier = TestEngineVerifier.Passing;
        IHeldDataSourceProbe probe = Service(store, TestChangeApprovals.CoveringConnections(), verifier: verifier);

        await probe.ProbeAsync(source.Id, CancellationToken.None);
        source.DisabledTime = DateTime.UtcNow;
        var disabled = () => probe.ProbeAsync(source.Id, CancellationToken.None);
        await disabled.Should().ThrowAsync<BeaconConflictException>().WithMessage(DataSourceService.DisabledApprovalMessage);
        await probe.ProbeAsync(999, CancellationToken.None);

        verifier.Calls.Should().Be(1);
    }

    // The approval's check refuses a source that no longer holds the definition its request proposes before anything
    // connects: the server is dialled only with a connection the reviewer was shown.
    [Test]
    public async Task TheApprovalProbe_OfAConnectionTheRequestDoesNotPropose_IsStale_AndConnectsToNothing()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, OtherDatabase, DataSourceApprovalDefinition.Held);
        var verifier = TestEngineVerifier.Passing;
        IHeldDataSourceProbe probe = Service(store, TestChangeApprovals.CoveringConnections(), verifier: verifier);

        var stale = () => probe.ProbeAsync(source.Id, HashOf(Connection), CancellationToken.None);

        await stale.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalService.ChangedSinceSubmittedMessage);
        verifier.Calls.Should().Be(0);
        await probe.ProbeAsync(source.Id, HashOf(Rotated.Replace("sales", "ledger", StringComparison.Ordinal)), CancellationToken.None);
        verifier.Calls.Should().Be(1, "a change of the secrets alone keeps the definition the request proposes");
    }

    [Test]
    public async Task TheApprovalProbe_OfAServerItCannotReach_Is409_KeepingTheCause()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, DataSourceApprovalDefinition.Held);

        var probe = () => ((IHeldDataSourceProbe)Service(store, TestChangeApprovals.CoveringConnections(), verifier: new UnreachableServer()))
            .ProbeAsync(source.Id, CancellationToken.None);

        var refused = (await probe.Should().ThrowAsync<BeaconConflictException>().WithMessage(DatabaseEngineVerifier.UnreachableMessage)).Which;
        refused.InnerException.Should().BeOfType<InvalidOperationException>().Which.InnerException.Should().BeOfType<TimeoutException>();
    }

    private static string HashOf(string connection, DataSourceType type = DataSourceType.Database)
    {
        var engine = type == DataSourceType.Database ? DatabaseEngineType.PostgreSQL : (DatabaseEngineType?)null;

        return TestTransport.Hasher().Hash(
            ConnectionDefinitionPurpose.DataSourceDefinition,
            DataSourceApprovalDefinition.Canonical(TestTransport.Policy(), type, engine, connection));
    }

    private static DataSource Stored(ChangeApprovalStore store, string connection, string? approvedHash, DataSourceType type = DataSourceType.Database)
    {
        var source = store.DataSources[0];
        source.DataSourceType = type;
        source.DatabaseEngineType = type == DataSourceType.Database ? DatabaseEngineType.PostgreSQL : null;
        source.EncryptedConnectionData = Encryption.Encrypt(connection);
        source.ApprovedTargetHash = approvedHash;

        return source;
    }

    private static DataSourceData Data(string connection, DataSource? source = null, bool confirmTargetChange = false, DataSourceType type = DataSourceType.Database) =>
        new()
        {
            DataSourceId = source?.Id,
            Name = source?.Name ?? "sales",
            DataSourceType = type,
            DatabaseEngineType = type == DataSourceType.Database ? DatabaseEngineType.PostgreSQL : null,
            ConnectionString = connection,
            ConfirmTargetChange = confirmTargetChange
        };

    private static IDataSourceConnectionResolver Resolver() =>
        TestTransport.Resolver(Encryption, configuration: TestChangeApprovals.CoveringConnections());

    private static DataSourceService Service(
        ChangeApprovalStore store,
        BeaconConfiguration configuration,
        IBeaconActorAccessor? caller = null,
        IDatabaseEngineVerifier? verifier = null,
        IBeaconScheduler? scheduler = null,
        ISecurityAuditRecorder? audit = null,
        IApprovalNotifier? notifier = null) =>
        new(
            store.Factory(),
            Encryption,
            TestProviderFactory.Accepting(),
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ISchemaGraphService>(),
            audit ?? new RecordingSecurityAudit(),
            TestTransport.Policy(),
            verifier ?? TestEngineVerifier.Passing,
            TestChangeApprovals.Submitter(configuration, caller ?? TestChangeApprovals.Caller(Ada), notifier),
            TestTransport.Hasher(),
            NullLogger<DataSourceService>.Instance,
            scheduler);

    // A server the engine check cannot reach: passes as an ordinary check does, refuses one that must reach it.
    private sealed class UnreachableServer : IDatabaseEngineVerifier
    {
        public Task<DatabaseEngineIdentification> IdentifyAsync(
            int? dataSourceId,
            DatabaseEngineType declared,
            System.Data.Common.DbConnection openConnection,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task EnsureDeclaredEngineAsync(int? dataSourceId, DatabaseEngineType declared, string connectionString, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task EnsureDeclaredEngineAsync(int? dataSourceId, DatabaseEngineType declared, string connectionString, bool refuseUnreachable, CancellationToken cancellationToken)
        {
            return refuseUnreachable
                ? Task.FromException(new InvalidOperationException(DatabaseEngineVerifier.UnreachableMessage, new TimeoutException()))
                : Task.CompletedTask;
        }
    }

    // A passing engine check that records how many transactions the store had begun when it was asked.
    private sealed class TransactionAwareVerifier(ChangeApprovalStore store) : IDatabaseEngineVerifier
    {
        public List<int> TransactionsOpenedWhenProbed { get; } = [];

        public Task<DatabaseEngineIdentification> IdentifyAsync(
            int? dataSourceId,
            DatabaseEngineType declared,
            System.Data.Common.DbConnection openConnection,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task EnsureDeclaredEngineAsync(int? dataSourceId, DatabaseEngineType declared, string connectionString, CancellationToken cancellationToken)
        {
            TransactionsOpenedWhenProbed.Add(store.Offline.TransactionIsolationLevels.Count);

            return Task.CompletedTask;
        }
    }

    // The engine verifier's answer for a server that is clearly another engine than the one declared.
    private sealed class MismatchedEngine : IDatabaseEngineVerifier
    {
        public const string Message = "The server is not PostgreSQL.";

        public List<(int? Id, string Connection)> Probed { get; } = [];

        public Task<DatabaseEngineIdentification> IdentifyAsync(
            int? dataSourceId,
            DatabaseEngineType declared,
            System.Data.Common.DbConnection openConnection,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task EnsureDeclaredEngineAsync(int? dataSourceId, DatabaseEngineType declared, string connectionString, CancellationToken cancellationToken)
        {
            Probed.Add((dataSourceId, connectionString));

            throw new InvalidOperationException(Message);
        }
    }
}
