using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Queries;
using Beacon.Core.Helpers;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// A query or step preview masks its rows for a caller who may run the query but not write its project (a Viewer), and
/// returns them as read to one who may write there (an Editor), decided by the project access evaluator.
/// </summary>
[TestFixture]
public class PreviewMaskingDecisionTests
{
    private const int ViewerProject = 1;
    private const int EditorProject = 2;
    private const int DataSourceId = 4;

    private static readonly ProjectAccessScope Member = TestProjectAccess.Scope(
        new Dictionary<int, ProjectRole> { [ViewerProject] = ProjectRole.Viewer, [EditorProject] = ProjectRole.Editor },
        includesOpen: false,
        notOpen: []);

    [TestCase(ViewerProject, true)]
    [TestCase(EditorProject, false)]
    public async Task AStoredQueryPreview_IsMaskedOnlyWhereTheCallerMayNotWrite(int holdingProject, bool masked)
    {
        var stored = new RecordingStoredQueryPreview { SnapshotSteps = [Step()] };

        var result = await ExecuteQueryPreviewHandler.RunAsync(stored, Authorizer(holdingProject), NullLogger.Instance, 5, null, new Paging(), CancellationToken.None);

        result!.Success.Should().BeTrue();
        stored.Masking.Should().Equal([masked]);
    }

    [TestCase(ViewerProject, true)]
    [TestCase(EditorProject, false)]
    public async Task AStepPreview_IsMaskedOnlyWhereTheCallerMayNotWrite(int holdingProject, bool masked)
    {
        var stored = new RecordingStoredQueryPreview { SnapshotSteps = [Step()] };

        var result = await ExecuteStepPreviewHandler.RunAsync(stored, Authorizer(holdingProject), NullLogger.Instance, 5, 1, null, null, new Paging(), CancellationToken.None);

        result!.Success.Should().BeTrue();
        stored.Masking.Should().Equal([masked]);
    }

    [Test]
    public async Task AnUnrestrictedCaller_GetsTheRowsAsRead()
    {
        var stored = new RecordingStoredQueryPreview { SnapshotSteps = [Step()] };

        await ExecuteQueryPreviewHandler.RunAsync(stored, TestProjectAccess.Authorizer(TestProjectAccess.Unrestricted), NullLogger.Instance, 5, null, new Paging(), CancellationToken.None);

        stored.Masking.Should().Equal([false]);
    }

    [Test]
    public async Task TheDecision_RefusesNothingAndLogsNoRefusal()
    {
        var log = new LogRecorder();
        var authorizer = TestProjectAccess.Authorizer(
            Member,
            new FixedResolver(ResolvedProjectResource.InProject(ViewerProject)),
            new SqlCapture().Factory(),
            logger: log.For<ProjectRequestAuthorizer>());

        var mayWrite = await authorizer.AllowsAsync(new ProjectCheck(ProjectResource.DataSources([DataSourceId]), ProjectAction.Write), CancellationToken.None);

        mayWrite.Should().BeFalse();
        log.Entries.Should().BeEmpty("a masked preview is an answer, not a refusal");
    }

    private static ProjectRequestAuthorizer Authorizer(int holdingProject) =>
        TestProjectAccess.Authorizer(Member, new FixedResolver(ResolvedProjectResource.InProject(holdingProject)), new SqlCapture().Factory());

    private static QueryStep Step() => new()
    {
        QueryId = 5,
        StepOrder = 1,
        DataSourceId = DataSourceId,
        DataSource = new DataSource { Id = DataSourceId, Name = "core", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "encrypted" },
        SqlValue = "SELECT id, email FROM customers"
    };

    private sealed class FixedResolver(ResolvedProjectResource resolved) : IProjectResourceResolver
    {
        public Task<ResolvedProjectResource> ResolveAsync(BeaconContext context, ProjectResource resource, CancellationToken cancellationToken)
        {
            return Task.FromResult(resolved);
        }
    }

    private sealed record Paging : ListRequest;
}
