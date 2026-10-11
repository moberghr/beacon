using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.DbtIntegration;
using Beacon.AI.Services.GitHub;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Code references belong to one project: a repository scan names its project and never reaches another project's
/// repository, and a dbt manifest is imported only into a project that links the data source it describes. Over
/// in-memory sets (no database, §4.7).
/// </summary>
[TestFixture]
public class CodeReferenceProjectScopeTests
{
    private const int ProjectId = 1;
    private const int OtherProjectId = 2;
    private const int OtherProjectsRepositoryId = 20;

    [Test]
    public async Task Scan_OfAnotherProjectsRepository_IsNotFound_AndReachesNoRepository()
    {
        var httpClients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var (factory, _) = Factory();
        var scanner = new GitHubScannerService(
            factory,
            new GitHubApiClient(httpClients.Object),
            [],
            Mock.Of<IEncryptionService>(),
            NullLogger<GitHubScannerService>.Instance);

        var scan = () => scanner.ScanRepositoryAsync(ProjectId, OtherProjectsRepositoryId);
        var progress = () => scanner.GetScanProgressAsync(ProjectId, OtherProjectsRepositoryId);

        await scan.Should().ThrowAsync<InvalidOperationException>().WithMessage($"Repository {OtherProjectsRepositoryId} not found");
        await progress.Should().ThrowAsync<InvalidOperationException>();
        httpClients.VerifyNoOtherCalls();
    }

    [Test]
    public async Task DbtImport_IntoAProjectThatDoesNotLinkTheDataSource_IsRefused_AndImportsNothing()
    {
        var (factory, saved) = Factory();
        var service = new DbtIntegrationService(factory, NullLogger<DbtIntegrationService>.Instance);
        var manifest = new DbtManifest { Models = [new DbtModel("orders", "sales", "Orders", "table", [], [])] };

        var act = () => service.ImportModelsAsync(ProjectId, 8, manifest);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"Project {ProjectId} does not link data source 8.");
        saved.Should().BeEmpty();
    }

    private static (IDbContextFactory<BeaconContext> Factory, List<object> Saved) Factory()
    {
        var saved = new List<object>();
        var sets = new Dictionary<Type, object>
        {
            [typeof(GitHubRepository)] = RecordingBeaconContext.MemorySet(
                [new GitHubRepository { Id = OtherProjectsRepositoryId, ProjectId = OtherProjectId, RepositoryUrl = "https://example.invalid/other" }],
                saved),
            [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(
                [new ProjectDataSource { ProjectId = OtherProjectId, DataSourceId = 8 }],
                saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));

        return (factory.Object, saved);
    }
}
