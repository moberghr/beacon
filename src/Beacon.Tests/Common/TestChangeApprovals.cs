using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.HostData;
using Beacon.Core.Notifications;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Beacon.Tests.Common;

/// <summary>
/// The change approval components (migration jobs, Custom SQL rules, recipient destinations) as the host wires them, for
/// tests: with the default configuration the workflow is off, so they change nothing a test without them expects.
/// </summary>
internal static class TestChangeApprovals
{
    /// <summary>The workflow on, covering every kind (the defaults), self-approval off.</summary>
    public static BeaconConfiguration On(bool allowSelfApproval = false)
    {
        return new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true, AllowSelfApproval = allowSelfApproval } };
    }

    /// <summary>The workflow on, covering data-source connections too (off by default), self-approval off.</summary>
    public static BeaconConfiguration CoveringConnections(bool allowSelfApproval = false)
    {
        return new BeaconConfiguration
        {
            ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true, AllowSelfApproval = allowSelfApproval, CoversDataSourceConnections = true }
        };
    }

    /// <summary>A signed-in, stored user as the caller: <paramref name="externalId"/>, <c>Users.Id</c> <paramref name="beaconUserId"/>.</summary>
    public static IBeaconActorAccessor Caller(string? externalId, int beaconUserId = 9, IReadOnlyList<string>? sessionIds = null, bool isAdmin = false)
    {
        var actor = externalId == null
            ? BeaconActor.Nobody
            : new BeaconActor(externalId, isAdmin)
            {
                Kind = BeaconPrincipalKind.User,
                BeaconUserId = beaconUserId,
                IsActiveUser = true,
                SessionIds = sessionIds ?? []
            };
        var accessor = new Mock<IBeaconActorAccessor>();
        accessor
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(actor);

        return accessor.Object;
    }

    public static ChangeApprovalSubmitter Submitter(
        BeaconConfiguration? configuration = null,
        IBeaconActorAccessor? caller = null,
        IApprovalNotifier? notifier = null)
    {
        return new ChangeApprovalSubmitter(
            caller ?? Caller(null),
            Mock.Of<IBeaconUserContext>(),
            configuration ?? new BeaconConfiguration(),
            NullLogger<ChangeApprovalSubmitter>.Instance,
            notifier);
    }

    /// <summary>The keyed hasher of where recipients deliver, under the test encryption key.</summary>
    public static RecipientDestinationHasher Hasher { get; } = new(TestEncryptionKey.Value);

    public static RecipientChangeApproval Recipients(
        RecipientSecretProtector protector,
        BeaconConfiguration? configuration = null,
        IBeaconActorAccessor? caller = null,
        IApprovalNotifier? notifier = null)
    {
        return new RecipientChangeApproval(Submitter(configuration, caller, notifier), protector, Hasher);
    }

    public static CustomSqlChangeApproval CustomSql(BeaconConfiguration? configuration = null, IBeaconActorAccessor? caller = null, IApprovalNotifier? notifier = null)
    {
        return new CustomSqlChangeApproval(Submitter(configuration, caller, notifier));
    }

    /// <summary>The delivery gate; with the workflow off (the default) it holds nothing and reads nothing.</summary>
    public static RecipientDeliveryApproval Deliveries(
        IDbContextFactory<BeaconContext>? factory = null,
        RecipientSecretProtector? protector = null,
        BeaconConfiguration? configuration = null,
        RecipientDestinationHasher? hasher = null)
    {
        return new RecipientDeliveryApproval(
            factory ?? Mock.Of<IDbContextFactory<BeaconContext>>(MockBehavior.Strict),
            protector ?? new RecipientSecretProtector(new EncryptionService(TestEncryptionKey.Value)),
            hasher ?? Hasher,
            configuration ?? new BeaconConfiguration());
    }

    public static MigrationJobApprovalState MigrationJobs(BeaconConfiguration? configuration = null)
    {
        return new MigrationJobApprovalState(configuration ?? new BeaconConfiguration());
    }

    /// <summary>
    /// The LLM connection's change approvals over <paramref name="factory"/> (by default one that must not be asked: the
    /// approval service hands its own context to everything but a save), with the settings stored and staged by Beacon's
    /// own <see cref="AppSettingsService"/> under the test encryption key (or staged by <paramref name="stager"/>), the tests'
    /// connection definition hasher, and the LLM connection lock recorded (<see cref="RecordingLlmConnectionLock"/>) unless
    /// <paramref name="connectionLock"/> is given. With the default catalog AI is off, so no provider is refused as not
    /// installed.
    /// </summary>
    public static LlmConnectionApproval Llm(
        IDbContextFactory<BeaconContext>? factory = null,
        BeaconConfiguration? configuration = null,
        IBeaconActorAccessor? caller = null,
        ISecurityAuditRecorder? audit = null,
        ILlmProviderCatalog? catalog = null,
        ILlmConfigurationUpdater? updater = null,
        IAppSettingsService? settingsService = null,
        IApprovalNotifier? notifier = null,
        IMemoryCache? cache = null,
        ConnectionDefinitionHasher? hasher = null,
        IEncryptionService? encryption = null,
        IAppSettingsStager? stager = null,
        ILlmConnectionLock? connectionLock = null,
        ILogger<LlmConnectionApproval>? logger = null)
    {
        factory ??= Mock.Of<IDbContextFactory<BeaconContext>>(MockBehavior.Strict);
        configuration ??= new BeaconConfiguration();
        audit ??= new RecordingSecurityAudit();
        encryption ??= new EncryptionService(TestEncryptionKey.Value);
        var settings = new AppSettingsService(factory, cache ?? new MemoryCache(new MemoryCacheOptions()), encryption, configuration, audit, updater);

        return new LlmConnectionApproval(
            factory,
            settingsService ?? settings,
            stager ?? settings,
            Submitter(configuration, caller, notifier),
            encryption,
            hasher ?? TestTransport.Hasher(),
            catalog ?? Mock.Of<ILlmProviderCatalog>(x => x.AiEnabled == false),
            audit,
            configuration,
            connectionLock ?? new RecordingLlmConnectionLock(),
            logger ?? NullLogger<LlmConnectionApproval>.Instance);
    }

    /// <summary>
    /// The reviewer's authority as the decision's checks before its transaction found it: nothing stored is read again
    /// (what a reviewer without a stored user gets), for tests about anything but a reviewer revoked mid-decision.
    /// </summary>
    public static IReviewerAuthority ReviewerUnchanged { get; } = new UnchangedReviewer();

    /// <summary>Beacon's own re-read of the reviewer, with the default role names and no request principal.</summary>
    public static IReviewerAuthority Reviewers(IBeaconRequestPrincipal? principal = null)
    {
        return new ReviewerAuthority(
            Microsoft.Extensions.Options.Options.Create(new Beacon.Core.Configuration.RoleMappingOptions()),
            TimeProvider.System,
            new BeaconPermissionLog(TimeProvider.System, NullLogger<BeaconPermissionLog>.Instance),
            principal ?? Mock.Of<IBeaconRequestPrincipal>());
    }

    /// <summary>The data-source connection parts of the approval service for tests that decide no connection: asking them fails.</summary>
    public static IDataSourceDefinitionReader NoDefinitions { get; } = new NoConnections();

    /// <inheritdoc cref="NoDefinitions"/>
    public static IHeldDataSourceProbe NoProbe { get; } = new NoConnections();

    /// <summary>A held-source probe that passes (or throws <paramref name="refusal"/>) and records what it was asked.</summary>
    public static RecordingHeldDataSourceProbe Probe(Exception? refusal = null) => new(refusal);

    private sealed class UnchangedReviewer : IReviewerAuthority
    {
        public Task<LiveReviewer?> ReadLockedAsync(BeaconContext context, BeaconActor reviewer, CancellationToken cancellationToken) =>
            Task.FromResult<LiveReviewer?>(null);
    }

    private sealed class NoConnections : IDataSourceDefinitionReader, IHeldDataSourceProbe
    {
        public string? CurrentDefinitionHash(DataSource dataSource) => throw Unexpected();

        public bool DefinitionMatches(DataSource dataSource, string? hash) => throw Unexpected();

        public Task ProbeAsync(int dataSourceId, CancellationToken cancellationToken) => throw Unexpected();

        public Task ConnectionApprovedAsync(int dataSourceId, CancellationToken cancellationToken) => throw Unexpected();

        private static InvalidOperationException Unexpected() => new("This test decides no data-source connection.");
    }
}

/// <summary>
/// Beacon's re-read of the reviewer with <c>revoke</c> run first, once: a change to the reviewer committed while the
/// decision waited for its lock. Records whether the decision's transaction was open when the reviewer was read again.
/// </summary>
internal sealed class RevokingReviewerAuthority(IReviewerAuthority inner, Action revoke) : IReviewerAuthority
{
    private bool _revoked;

    public int Reads { get; private set; }

    public bool ReadInsideTheTransaction { get; private set; }

    public Task<LiveReviewer?> ReadLockedAsync(BeaconContext context, BeaconActor reviewer, CancellationToken cancellationToken)
    {
        if (!_revoked)
        {
            _revoked = true;
            revoke();
        }

        Reads++;
        ReadInsideTheTransaction = context.Database.CurrentTransaction != null;

        return inner.ReadLockedAsync(context, reviewer, cancellationToken);
    }
}

/// <summary>A held-source probe for tests: records the probes and the approvals it was told about.</summary>
internal sealed class RecordingHeldDataSourceProbe(Exception? refusal) : IHeldDataSourceProbe
{
    public List<int> Probed { get; } = [];

    public List<int> Approved { get; } = [];

    public Task ProbeAsync(int dataSourceId, CancellationToken cancellationToken)
    {
        Probed.Add(dataSourceId);

        return refusal == null ? Task.CompletedTask : Task.FromException(refusal);
    }

    public Task ConnectionApprovedAsync(int dataSourceId, CancellationToken cancellationToken)
    {
        Approved.Add(dataSourceId);

        return Task.CompletedTask;
    }
}
