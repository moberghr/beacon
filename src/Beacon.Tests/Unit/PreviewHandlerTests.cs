using Beacon.Core.Exceptions;
using Beacon.Core.Models;
using Beacon.Core.Handlers.Queries;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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

        var result = await QueryHandler(stored).Handle(new ExecuteQueryPreviewCommand { QueryId = 5, Page = 1, PageSize = 20 }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(Cause);
        stored.Loads.Should().Equal(5);
    }

    [Test]
    public async Task AQueryPreviewFailure_InALibrarysWords_ComesBackAsBeaconsGenericWording()
    {
        var stored = new RecordingStoredQueryPreview { Answer = () => throw new IOException("db.internal:5432 refused user 'reporting' for value 'alice@example.com'") };

        var result = await QueryHandler(stored).Handle(new ExecuteQueryPreviewCommand { QueryId = 5, Page = 1, PageSize = 20 }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(QueryService.PreviewFailedMessage);
    }

    [Test]
    public async Task AStepPreviewFailure_AsTheQueryPathRedactedIt_ComesBackAsRedacted()
    {
        const string redacted = "Query execution failed: column [value] does not exist";
        var stored = new RecordingStoredQueryPreview { Answer = () => throw new QueryExecutionFailedException(redacted) };

        var result = await StepHandler(stored).Handle(new ExecuteStepPreviewCommand { QueryId = 5, StepOrder = 2, Page = 0, PageSize = 20 }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(redacted);
    }

    [Test]
    public void APreviewFailure_OnlyBeaconsOwnTextOrARedactedError_IsShownAsWritten()
    {
        QueryService.PreviewFailureText(new BeaconException(Cause)).Should().Be(Cause);
        QueryService.PreviewFailureText(new QueryExecutionFailedException("redacted")).Should().Be("redacted");
        QueryService.PreviewFailureText(new TimeoutException("host db.internal timed out")).Should().Be(QueryService.PreviewFailedMessage);
        QueryService.PreviewFailureText(new InvalidOperationException("never thrown: no throwing method to read")).Should().Be(QueryService.PreviewFailedMessage);
    }

    [Test]
    public async Task ACancelledQueryPreview_ThrowsThePreviewFailedError()
    {
        var stored = new RecordingStoredQueryPreview { Answer = () => throw new OperationCanceledException() };

        var act = () => QueryHandler(stored).Handle(new ExecuteQueryPreviewCommand { QueryId = 5, Page = 0, PageSize = 20 }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Query #5 preview failed.");
    }

    [Test]
    public async Task AQueryPreviewDraft_RunsItsOwnStepsAndNeverLoadsTheStoredQuery()
    {
        var draft = new QueryDraft { FinalQuery = "SELECT * FROM @result1" };
        var stored = new RecordingStoredQueryPreview();

        var result = await QueryHandler(stored).Handle(new ExecuteQueryPreviewCommand { QueryId = 5, Draft = draft, Page = 0, PageSize = 20 }, CancellationToken.None);

        result.Success.Should().BeTrue();
        stored.DraftRuns.Should().ContainSingle().Which.Draft.Should().BeSameAs(draft);
        stored.Loads.Should().BeEmpty();
    }

    [Test]
    public async Task AStepPreviewFailure_ComesBackAsTheResultWithItsCause()
    {
        var stored = new RecordingStoredQueryPreview { Answer = () => throw new BeaconException(Cause) };

        var result = await StepHandler(stored).Handle(new ExecuteStepPreviewCommand { QueryId = 5, StepOrder = 2, Page = 0, PageSize = 20 }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(Cause);
        stored.Runs.Should().ContainSingle().Which.StepOrder.Should().Be(2);
    }

    [Test]
    public async Task ACancelledStepPreview_ThrowsThePreviewFailedError()
    {
        var stored = new RecordingStoredQueryPreview { Answer = () => throw new OperationCanceledException() };

        var act = () => StepHandler(stored).Handle(new ExecuteStepPreviewCommand { QueryId = 5, StepOrder = 2, Page = 0, PageSize = 20 }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Step preview failed for query #5 step 2.");
    }

    private static ExecuteQueryPreviewHandler QueryHandler(RecordingStoredQueryPreview stored) =>
        new(stored, TestProjectAccess.Authorizer(TestProjectAccess.Unrestricted), NullLogger<ExecuteQueryPreviewHandler>.Instance);

    private static ExecuteStepPreviewHandler StepHandler(RecordingStoredQueryPreview stored) =>
        new(stored, TestProjectAccess.Authorizer(TestProjectAccess.Unrestricted), NullLogger<ExecuteStepPreviewHandler>.Instance);
}
