using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Beacon.Core.Handlers.Tasks;
using Beacon.Core.Models.Tasks;
using Beacon.Core.Services;
using Beacon.Core.Authorization;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Tasks;

/// <summary>Pins the SQL of the task reads whose logic lives in their handlers.</summary>
[TestFixture]
public class TaskReadQueriesTranslateTests
{
    private const int TaskId = 60;

    [Test]
    public async Task TaskList_FilteredByResolvedAndSubscription_Translates()
    {
        var capture = new SqlCapture().ThenScalar(0);

        var page = await new GetTasksHandler(capture.Factory(), null!).Read(
            new GetTasksRequest { Readable = ProjectSet.Unrestricted, SubscriptionId = 5, Resolved = true },
            CancellationToken.None);

        page.Items.Should().BeEmpty();
        var count = capture.Commands.Should().ContainSingle("an empty count skips the page query").Subject;
        count.Should().Contain("FROM beacon.query_tasks");
        count.Should().Contain("subscription_id");
        count.Should().Contain("resolved");
    }

    [Test]
    public async Task RelatedTasks_Translates()
    {
        var capture = new SqlCapture().ThenScalar(7).ThenNoRows();

        var related = await new GetTaskRelatedHandler(capture.Factory()).Read(TaskId, CancellationToken.None);

        related.Should().BeEmpty();
        capture.Commands.Should().HaveCount(2, "the task's query, then the other tasks of that query");
        capture.Commands[1].Should().Contain("LIMIT").And.Contain("ORDER BY");
    }

    [Test]
    public async Task RelatedTasks_UnknownTask_ReadsNoSecondQuery()
    {
        var capture = new SqlCapture().ThenNoRows();

        var related = await new GetTaskRelatedHandler(capture.Factory()).Read(TaskId, CancellationToken.None);

        related.Should().BeEmpty();
        capture.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task TaskComments_Translates()
    {
        var capture = new SqlCapture().ThenNoRows();

        var comments = await new GetTaskCommentsHandler(capture.Factory()).Read(TaskId, CancellationToken.None);

        comments.Should().BeEmpty();
        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("FROM beacon.comments").And.Contain("ORDER BY");
    }

    [Test]
    public async Task AddTaskComment_UnknownTask_Throws()
    {
        var capture = new SqlCapture().ThenScalar(false);

        var act = () => new AddTaskCommentHandler(capture.Factory(), null!).Add(TaskId, "c", "u", "n", CancellationToken.None);

        await act.Should().ThrowAsync<Beacon.Core.Models.BeaconException>();
        capture.Commands.Should().ContainSingle().Which.Should().Contain("FROM beacon.query_tasks");
    }

    [Test]
#pragma warning disable CS0618 // the obsolete service members are the surface under test
    public async Task ObsoleteTaskServiceForwarders_ReadThroughTheHandlers()
    {
        var capture = new SqlCapture().ThenNoRows().ThenScalar(3).ThenNoRows();
        var service = new TaskService(capture.Factory(), NullLogger<TaskService>.Instance, new RecordingSecurityAudit());

        var comments = await service.GetTaskComments(TaskId, CancellationToken.None);
        var related = await service.GetRelatedTasks(TaskId, CancellationToken.None);

        comments.Should().BeEmpty();
        related.Should().BeEmpty();
        capture.Commands.Should().HaveCount(3);
        capture.Commands[0].Should().Contain("FROM beacon.comments");
    }
#pragma warning restore CS0618
}
