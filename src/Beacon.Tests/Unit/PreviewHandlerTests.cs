using Beacon.Core.Models;
using Beacon.Core.Handlers.Queries;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>The preview handlers carry the logic the preview service used to hold; the obsolete service forwards to them.</summary>
[TestFixture]
public class PreviewHandlerTests
{
    private const string Cause = "No connection factory registered for database engine: MSSQL.";

    [Test]
    public async Task AQueryPreviewFailure_ComesBackAsTheResultWithItsCause()
    {
        var stored = new RecordingStoredQueryPreview { Answer = () => throw new BeaconException(Cause) };

        var result = await QueryHandler(new Mock<IQueryService>(), stored).Handle(new ExecuteQueryPreviewCommand { QueryId = 5, Page = 1, PageSize = 20 }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(Cause);
        stored.Loads.Should().Equal(5);
    }

    [Test]
    public async Task ACancelledQueryPreview_ThrowsThePreviewFailedError()
    {
        var stored = new RecordingStoredQueryPreview { Answer = () => throw new OperationCanceledException() };

        var act = () => QueryHandler(new Mock<IQueryService>(), stored).Handle(new ExecuteQueryPreviewCommand { QueryId = 5, Page = 0, PageSize = 20 }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Query #5 preview failed.");
    }

    [Test]
    public async Task AQueryPreviewDraft_RunsItsOwnStepsAndNeverLoadsTheStoredQuery()
    {
        var draft = new QueryDraft { FinalQuery = "SELECT * FROM @result1" };
        var queryService = new Mock<IQueryService>();
        queryService
            .Setup(x => x.PreviewQuery(5, draft, It.IsAny<ListRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryPreviewResult { Success = true });
        var stored = new RecordingStoredQueryPreview();

        var result = await QueryHandler(queryService, stored).Handle(new ExecuteQueryPreviewCommand { QueryId = 5, Draft = draft, Page = 0, PageSize = 20 }, CancellationToken.None);

        result.Success.Should().BeTrue();
        stored.Loads.Should().BeEmpty();
    }

    [Test]
    public async Task AStepPreviewFailure_ComesBackAsTheResultWithItsCause()
    {
        var stored = new RecordingStoredQueryPreview { Answer = () => throw new BeaconException(Cause) };

        var result = await StepHandler(new Mock<IQueryService>(), stored).Handle(new ExecuteStepPreviewCommand { QueryId = 5, StepOrder = 2, Page = 0, PageSize = 20 }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(Cause);
        stored.Runs.Should().ContainSingle().Which.StepOrder.Should().Be(2);
    }

    [Test]
    public async Task ACancelledStepPreview_ThrowsThePreviewFailedError()
    {
        var stored = new RecordingStoredQueryPreview { Answer = () => throw new OperationCanceledException() };

        var act = () => StepHandler(new Mock<IQueryService>(), stored).Handle(new ExecuteStepPreviewCommand { QueryId = 5, StepOrder = 2, Page = 0, PageSize = 20 }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Step preview failed for query #5 step 2.");
    }

    private static ExecuteQueryPreviewHandler QueryHandler(Mock<IQueryService> queryService, RecordingStoredQueryPreview stored) =>
        new(queryService.Object, stored, TestProjectAccess.Authorizer(TestProjectAccess.Unrestricted), NullLogger<ExecuteQueryPreviewHandler>.Instance);

    private static ExecuteStepPreviewHandler StepHandler(Mock<IQueryService> queryService, RecordingStoredQueryPreview stored) =>
        new(queryService.Object, stored, TestProjectAccess.Authorizer(TestProjectAccess.Unrestricted), NullLogger<ExecuteStepPreviewHandler>.Instance);
}
