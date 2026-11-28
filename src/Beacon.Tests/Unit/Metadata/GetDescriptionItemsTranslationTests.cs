using Beacon.Core.Authorization;
using Beacon.Core.Handlers.DataSources;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Metadata;

/// <summary>
/// A data source's description items page in SQL on both providers (§4.7, no database): a count, then one page of the
/// source's items ordered by schema, table and column and ended on the item id, the writer's label read in the same
/// statement. The request is checked as a read of the data source's project.
/// </summary>
[TestFixture]
public class GetDescriptionItemsTranslationTests
{
    [TestCase(false, "m.data_source_id = @", "ORDER BY m.schema_name, m.table_name, m.column_name, m.id", "LIMIT @", "users")]
    [TestCase(true, "[m].[DataSourceId] = @", "ORDER BY [m].[SchemaName], [m].[TableName], [m].[ColumnName], [m].[Id]", "FETCH NEXT @", "[Users]")]
    public async Task GetDescriptionItems_Translates(bool sqlServer, string bySource, string order, string paging, string users)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture()).AnsweringCountsWithOne();

        var page = await new GetDescriptionItemsHandler(capture.Factory())
            .Handle(new GetDescriptionItemsQuery { DataSourceId = 7, Page = 2, PageSize = 10 }, CancellationToken.None);

        capture.Commands.Should().HaveCount(2, "the count, then the page");
        capture.Commands.Should().OnlyContain(x => x.Contains(bySource));
        capture.CommandParameters[0].Values.Should().Contain(7);
        capture.Commands[1].Should().Contain(order).And.Contain(paging).And.Contain(users, "the writer's label is read in the page's statement");
        capture.CommandParameters[1].Values.Should().Contain(20, "page 2 of 10 rows starts at row 20");
        page.TotalCount.Should().Be(1);
    }

    [Test]
    public async Task ARequestedSort_ReplacesTheDefault_Translates()
    {
        var capture = new SqlCapture().AnsweringCountsWithOne();

        await new GetDescriptionItemsHandler(capture.Factory())
            .Handle(new GetDescriptionItemsQuery { DataSourceId = 7, Sort = "-staleSince" }, CancellationToken.None);

        capture.Commands[1].Should().Contain("ORDER BY m.stale_since DESC, m.id");
    }

    [Test]
    public void TheRequest_IsAReadOfTheDataSourcesProject()
    {
        IProjectAuthorizedRequest request = new GetDescriptionItemsQuery { DataSourceId = 7 };

        request.ProjectChecks.Should().BeEquivalentTo([new ProjectCheck(ProjectResource.DataSource(7), ProjectAction.Read)]);
    }

    [Test]
    public async Task ADataSourceIdThatIsNotPositive_IsRefusedWithoutAQuery()
    {
        var capture = new SqlCapture();

        var act = () => new GetDescriptionItemsHandler(capture.Factory())
            .Handle(new GetDescriptionItemsQuery { DataSourceId = 0 }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        capture.Commands.Should().BeEmpty();
    }
}
