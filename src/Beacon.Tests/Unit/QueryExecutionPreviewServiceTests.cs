using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

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

    [Test]
    public async Task ExecuteQueryPreview_ExecutionThrows_ReturnsFailedResultWithCause()
    {
        var queryService = new Mock<IQueryService>();
        queryService
            .Setup(x => x.PreviewQuery(5, null, It.IsAny<ListRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BeaconException(Cause));

        var result = await CreateService(queryService).ExecuteQueryPreview(5, null, SecondPage, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(Cause);
    }

    [Test]
    public async Task ExecuteStepPreview_PassesPagingAndReturnsFailedResultWithCause()
    {
        var queryService = new Mock<IQueryService>();
        queryService
            .Setup(x => x.PreviewQueryStepPaged(5, 2, It.IsAny<List<ParameterValue>?>(), null, SecondPage, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BeaconException(Cause));

        var result = await CreateService(queryService).ExecuteStepPreview(5, 2, [], null, SecondPage, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(Cause);
        queryService.Verify(x => x.PreviewQueryStepPaged(5, 2, It.IsAny<List<ParameterValue>?>(), null, SecondPage, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ExecuteQueryPreview_ForwardsTheEditorDraft()
    {
        var draft = new QueryDraft { FinalQuery = "SELECT * FROM @result1" };
        var queryService = new Mock<IQueryService>();
        queryService
            .Setup(x => x.PreviewQuery(5, draft, SecondPage, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryPreviewResult { Success = true });

        var result = await CreateService(queryService).ExecuteQueryPreview(5, draft, SecondPage, CancellationToken.None);

        result!.Success.Should().BeTrue();
        queryService.Verify(x => x.PreviewQuery(5, draft, SecondPage, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ExecuteQueryPreview_Cancelled_StillReturnsNull()
    {
        var queryService = new Mock<IQueryService>();
        queryService
            .Setup(x => x.PreviewQuery(5, null, It.IsAny<ListRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var result = await CreateService(queryService).ExecuteQueryPreview(5, null, SecondPage, CancellationToken.None);

        result.Should().BeNull("a cancelled preview is not a failure to report");
    }

    private static IQueryExecutionPreviewService CreateService(Mock<IQueryService> queryService) =>
        new QueryExecutionPreviewService(queryService.Object, NullLogger<QueryExecutionPreviewService>.Instance);

    private sealed record Paging : ListRequest;
}
