using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Handlers.Projects;
using Beacon.AI.Services.GitHub;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// A scan reads a project's code with its stored repository credentials: the request is recorded with the project and the
/// repository ids, never a repository's address.
/// </summary>
[TestFixture]
public class RepositoryScanEventTests
{
    [Test]
    public async Task AScanRequest_IsRecorded_WithTheProjectAndItsRepositoryIds()
    {
        var saved = new List<object>();
        var repositories = new List<GitHubRepository>
        {
            new() { Id = 9, ProjectId = 4, RepositoryUrl = "https://example.test/org/finance" },
            new() { Id = 12, ProjectId = 4, RepositoryUrl = "https://example.test/org/finance-etl" },
            new() { Id = 30, ProjectId = 5, RepositoryUrl = "https://example.test/org/other" }
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(new Dictionary<Type, object> { [typeof(GitHubRepository)] = RecordingBeaconContext.MemorySet(repositories, saved) }, saved));
        var audit = new RecordingSecurityAudit(new BeaconActor("ext-ada", false) { Kind = BeaconPrincipalKind.User, BeaconUserId = 3, IsActiveUser = true });

        await new ScanAllRepositoriesHandler(factory.Object, Mock.Of<IGitHubScannerService>(), TestProjectAccess.Actors(), audit, NullLogger<ScanAllRepositoriesHandler>.Instance)
            .Handle(new ScanAllRepositoriesCommand(4), CancellationToken.None);

        audit.Single("example.test", "finance").Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.RepositoryScanRequested, ActorUserId = (int?)3, ProjectId = (int?)4, ResourceId = "4", DetailsJson = "{\"repositoryIds\":[9,12]}" },
            x => x.ExcludingMissingMembers());
        audit.Ways.Should().Equal("recorded");
    }
}
