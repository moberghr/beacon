using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Authorization;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>Archiving an AI actor is on record once, and archiving an archived actor changes nothing.</summary>
[TestFixture]
public class AiActorArchiveTests
{
    private const int ActorId = 9;

    [Test]
    public async Task Archive_ArchivesTheActor_AndStagesOneEventInTheSameSave()
    {
        var actor = new AiActor { Id = ActorId, Name = "watcher", Instructions = "watch", DataSourceId = 1, Status = AiActorStatus.Active };
        var audit = new RecordingSecurityAudit();

        await Service(actor, audit).ArchiveActorAsync(ActorId, CancellationToken.None);

        actor.Status.Should().Be(AiActorStatus.Archived);
        actor.ArchivedTime.Should().NotBeNull();
        var archived = audit.Events.Should().ContainSingle().Subject;
        archived.Action.Should().Be(SecurityAuditActions.AiActorArchived);
        archived.ResourceType.Should().Be(SecurityAuditResources.AiActor);
        archived.ResourceId.Should().Be("9");
        audit.Ways.Should().Equal("staged");
        audit.AssertStagedEventsSaved();
    }

    [Test]
    public async Task Archive_OfAnArchivedActor_KeepsItsArchivedTime_AndRecordsNothing()
    {
        var first = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var actor = new AiActor { Id = ActorId, Name = "watcher", Instructions = "watch", DataSourceId = 1, Status = AiActorStatus.Archived, ArchivedTime = first };
        var audit = new RecordingSecurityAudit();

        await Service(actor, audit).ArchiveActorAsync(ActorId, CancellationToken.None);

        actor.ArchivedTime.Should().Be(first);
        audit.Events.Should().BeEmpty();
    }

    private static AiActorService Service(AiActor actor, RecordingSecurityAudit audit)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object> { [typeof(AiActor)] = RecordingBeaconContext.MemorySet(new List<AiActor> { actor }, []) },
                []));

        return new AiActorService(
            factory.Object,
            Mock.Of<ILlmProvider>(MockBehavior.Strict),
            Mock.Of<IDatabaseMetadataService>(MockBehavior.Strict),
            Mock.Of<IQueryService>(MockBehavior.Strict),
            Mock.Of<ISubscriptionService>(MockBehavior.Strict),
            Mock.Of<IBeaconScheduler>(MockBehavior.Strict),
            Mock.Of<IRunAsScope>(),
            TestProjectAccess.ExecutionAllowed(),
            Mock.Of<IStepSqlSaveCheck>(),
            Mock.Of<IQueryProposalCheck>(MockBehavior.Strict),
            Mock.Of<IQueryVersionService>(),
            new Beacon.Core.BeaconConfiguration(),
            audit,
            NullLogger<AiActorService>.Instance);
    }
}
