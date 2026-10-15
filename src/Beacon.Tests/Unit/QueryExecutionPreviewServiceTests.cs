using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// A failed preview must carry its cause back to the user. Returning null left the handlers only
/// "Query #N preview failed.", with the real error (e.g. a missing connector) buried in the host log.
/// </summary>
[TestFixture]
public class QueryExecutionPreviewServiceTests
{
    private const string Cause = "No connection factory registered for database engine: MSSQL.";

    private static readonly ListRequest SecondPage = new Paging { Page = 1, PageSize = 20, Sort = "-name" };

    // A preview of unsaved steps runs a temporary query once and deletes it: it is stored live through the preview seam,
    // never through IQueryService.CreateQuery, which with the approval workflow on would store nothing live to run.
    [Test]
    public async Task ATemporaryPreview_StoresItsQueryThroughThePreviewSeam()
    {
        var queryService = new Mock<IQueryService>();
        var stored = new RecordingStoredQueryPreview();
        var draft = new QueryData { Name = "draft", Steps = [new QueryStepData { StepOrder = 1, DataSourceId = 3, SqlValue = "SELECT 1" }] };

        await CreateService(queryService, stored).ExecuteTemporaryQueryPreview(draft, CancellationToken.None);
        await CreateService(queryService, stored).ExecuteTemporaryStepPreview(draft, 1, null, CancellationToken.None);

        stored.TemporaryQueries.Should().HaveCount(2).And.OnlyContain(x => x.Steps.Single().SqlValue == "SELECT 1");
        queryService.Verify(x => x.CreateQuery(It.IsAny<QueryData>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ExecuteQueryPreview_ExecutionThrows_ReturnsFailedResultWithCause()
    {
        var stored = new RecordingStoredQueryPreview { Answer = () => throw new BeaconException(Cause) };

        var result = await CreateService(new Mock<IQueryService>(), stored).ExecuteQueryPreview(5, null, SecondPage, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(Cause);
    }

    [Test]
    public async Task ExecuteStepPreview_PassesPagingAndReturnsFailedResultWithCause()
    {
        var stored = new RecordingStoredQueryPreview { Answer = () => throw new BeaconException(Cause) };

        var result = await CreateService(new Mock<IQueryService>(), stored).ExecuteStepPreview(5, 2, [], null, SecondPage, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(Cause);
        stored.Loads.Should().Equal(5);
        stored.Runs.Should().Equal([(2, SecondPage)]);
    }

    [Test]
    public async Task ExecuteQueryPreview_ForwardsTheEditorDraft()
    {
        var draft = new QueryDraft { FinalQuery = "SELECT * FROM @result1" };
        var queryService = new Mock<IQueryService>();
        queryService
            .Setup(x => x.PreviewQuery(5, draft, SecondPage, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryPreviewResult { Success = true });
        var stored = new RecordingStoredQueryPreview();

        var result = await CreateService(queryService, stored).ExecuteQueryPreview(5, draft, SecondPage, CancellationToken.None);

        result!.Success.Should().BeTrue();
        queryService.Verify(x => x.PreviewQuery(5, draft, SecondPage, It.IsAny<CancellationToken>()), Times.Once);
        stored.Loads.Should().BeEmpty("a draft runs its own steps, never the stored query");
    }

    [Test]
    public async Task ExecuteQueryPreview_Cancelled_StillReturnsNull()
    {
        var stored = new RecordingStoredQueryPreview { Answer = () => throw new OperationCanceledException() };

        var result = await CreateService(new Mock<IQueryService>(), stored).ExecuteQueryPreview(5, null, SecondPage, CancellationToken.None);

        result.Should().BeNull("a cancelled preview is not a failure to report");
    }

    private static IQueryExecutionPreviewService CreateService(Mock<IQueryService> queryService, RecordingStoredQueryPreview stored) =>
        new QueryExecutionPreviewService(
            queryService.Object,
            stored,
            TestProjectAccess.Authorizer(TestProjectAccess.Unrestricted),
            NullLogger<QueryExecutionPreviewService>.Instance);

    private sealed record Paging : ListRequest;
}
