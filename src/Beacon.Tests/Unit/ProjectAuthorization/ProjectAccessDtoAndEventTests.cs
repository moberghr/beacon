using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// What a project's detail says about access (restricted, the caller's role, whether it may manage members, who manages
/// membership, and, for one who may, the sources another open project keeps reachable), and the security log events of
/// repository actions.
/// </summary>
[TestFixture]
public class ProjectAccessDtoAndEventTests
{
    private const int ProjectId = 2;

    [Test]
    public async Task AProjectAdmin_SeesItMayManageMembers_AndTheOpenlyReachableSources()
    {
        var scope = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [ProjectId] = ProjectRole.ProjectAdmin }, includesOpen: true, notOpen: []);
        var capture = Detail().ThenRows([4], [6]);

        var detail = (await Handler(capture, scope).Handle(new GetProjectDetailQuery(ProjectId), CancellationToken.None)).Project!;

        detail.RequiresMembership.Should().BeTrue();
        detail.MyRole.Should().Be(ProjectRole.ProjectAdmin);
        detail.CanManageMembers.Should().BeTrue();
        detail.MembershipManagement.Should().Be(ProjectMembershipManagement.Beacon);
        detail.OpenlyReachableDataSourceIds.Should().Equal(4, 6);
        capture.Commands.Last().Should().Contain("requires_membership", "only links to another live open project keep a source reachable");
    }

    [Test]
    public async Task AnEditor_SeesItsRole_ButNotTheMembersOrTheSources()
    {
        var scope = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [ProjectId] = ProjectRole.Editor }, includesOpen: true, notOpen: []);
        var capture = Detail();

        var detail = (await Handler(capture, scope).Handle(new GetProjectDetailQuery(ProjectId), CancellationToken.None)).Project!;

        detail.MyRole.Should().Be(ProjectRole.Editor);
        detail.CanManageMembers.Should().BeFalse();
        detail.OpenlyReachableDataSourceIds.Should().BeEmpty();
        capture.Commands.Should().HaveCount(4, "the reachable sources are read only for a caller who may manage members");
    }

    [Test]
    public async Task WhileTheHostManagesMembership_NobodyManagesMembersHere()
    {
        var capture = Detail().ThenNoRows().ThenNoRows();

        var detail = (await Handler(capture, TestProjectAccess.Unrestricted, Mock.Of<IProjectMembershipSource>(x => x.IsAvailable == true))
            .Handle(new GetProjectDetailQuery(ProjectId), CancellationToken.None)).Project!;

        detail.CanManageMembers.Should().BeFalse();
        detail.MembershipManagement.Should().Be(ProjectMembershipManagement.Host);
    }

    [Test]
    public async Task WithoutUserManagement_MembershipIsUnavailable_NotHostManaged()
    {
        var capture = Detail().ThenNoRows().ThenNoRows();
        var withoutUserManagement = new DatabaseProjectMembershipSource(new BeaconConfiguration(), capture.Factory());

        var detail = (await Handler(capture, TestProjectAccess.Unrestricted, withoutUserManagement)
            .Handle(new GetProjectDetailQuery(ProjectId), CancellationToken.None)).Project!;

        detail.CanManageMembers.Should().BeFalse();
        detail.MembershipManagement.Should().Be(ProjectMembershipManagement.Unavailable);
    }

    [Test]
    public void TheMembershipRoutes_SayWhyTheyAreClosed()
    {
        var host = Mock.Of<IProjectMembershipSource>(x => x.IsAvailable == true);
        var unavailable = Mock.Of<IProjectMembershipSource>(x => x.IsAvailable == false);

        FluentActions.Invoking(() => ProjectMembershipRules.EnsureManagedByBeacon(host))
            .Should().Throw<BeaconConflictException>().WithMessage(ProjectMembershipRules.ManagedByHostMessage);
        FluentActions.Invoking(() => ProjectMembershipRules.EnsureManagedByBeacon(unavailable))
            .Should().Throw<BeaconConflictException>().WithMessage(ProjectMembershipRules.UnavailableMessage);
    }

    [Test]
    public async Task ChangingARepositoryToken_IsASecurityEvent_WithIdsOnly()
    {
        var saved = new List<object>();
        var repositories = new List<GitHubRepository> { new() { Id = 9, ProjectId = ProjectId, RepositoryUrl = "https://example.test/repo" } };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object> { [typeof(GitHubRepository)] = RecordingBeaconContext.MemorySet(repositories, saved) },
                saved));
        var logs = new LogRecorder();
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(x => x.Encrypt(It.IsAny<string>())).Returns("enc");
        var handler = new UpdateRepositoryTokenHandler(factory.Object, encryption.Object, TestProjectAccess.Actors(), logs.For<UpdateRepositoryTokenHandler>());

        await handler.Handle(new UpdateRepositoryTokenCommand(9, "ghp_secret_value"), CancellationToken.None);

        logs.Entries.Should().ContainSingle(x => x.Message.StartsWith("Repository access token set", StringComparison.Ordinal))
            .Which.Message.Should().Contain("repository 9").And.Contain($"project {ProjectId}");
        logs.Entries.Should().NotContain(x => x.FullText.Contains("ghp_secret_value"));
    }

    // The project row (restricted), no data sources, no repositories, no documentation.
    private static SqlCapture Detail()
    {
        return new SqlCapture()
            .ThenRow(ProjectId, "Finance", "desc", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), true)
            .ThenNoRows()
            .ThenNoRows()
            .ThenScalar(false);
    }

    private static GetProjectDetailHandler Handler(SqlCapture capture, ProjectAccessScope scope, IProjectMembershipSource? membership = null)
    {
        var configuration = new BeaconConfiguration();
        configuration.EnableUserManagement();

        return new GetProjectDetailHandler(
            capture.Factory(),
            TestProjectAccess.Evaluator(scope),
            membership ?? new DatabaseProjectMembershipSource(configuration, capture.Factory()),
            SettingsProviderMock.Create().Object,
            Options.Create(new ProjectAuthorizationOptions()));
    }
}
