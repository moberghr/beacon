using Beacon.AI.Services.Learning;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Beacon.Tests.Integration;

/// <summary>Translation coverage (§4.3) for the batched metadata lookups the learning aggregation runs once per pass.</summary>
[TestFixture]
public class LearningBatchedLookupTranslationTests : QueryTranslationTestBase
{
    [Test]
    public void ColumnExistenceBatch_Translates()
    {
        int[] dataSourceIds = [1, 2];
        List<string?> tableNames = ["orders", "items"];
        List<string?> columnNames = ["created_at", "total"];

        var sql = Context.ColumnMetadata
            .Where(x => dataSourceIds.Contains(x.DatabaseMetadata.DataSourceId))
            .Where(x => tableNames.Contains(x.DatabaseMetadata.TableName))
            .Where(x => columnNames.Contains(x.ColumnName))
            .Select(x =>
                new
                {
                    x.DatabaseMetadata.DataSourceId,
                    x.DatabaseMetadata.SchemaName,
                    x.DatabaseMetadata.TableName,
                    x.ColumnName,
                    x.Description
                })
            .ToQueryString();

        sql.Should().Contain("data_source_id");
        sql.Should().Contain("table_name");
        sql.Should().Contain("column_name");
        sql.Should().Contain("description");
    }

    [Test]
    public void TableDescriptionBatch_Translates()
    {
        int[] dataSourceIds = [1, 2];
        List<string?> tableNames = ["orders", "items"];

        var sql = Context.DatabaseMetadata
            .Where(x => dataSourceIds.Contains(x.DataSourceId))
            .Where(x => tableNames.Contains(x.TableName))
            .Select(x =>
                new
                {
                    x.DataSourceId,
                    x.SchemaName,
                    x.TableName,
                    x.TableDescription
                })
            .ToQueryString();

        sql.Should().Contain("table_name");
        sql.Should().Contain("table_description");
    }

    [Test]
    public void ColumnExistenceBatch_SqlServer_FullBatchStaysUnderTheParameterLimit()
    {
        using var context = SqlServerTestContext.Create();
        var size = McpLearningAggregationService.LookupBatchSize;
        var dataSourceIds = Enumerable.Range(1, size).ToList();
        var tableNames = Enumerable.Range(1, size).Select(x => (string?)$"t{x}").ToList();
        var columnNames = Enumerable.Range(1, size).Select(x => (string?)$"c{x}").ToList();

        var sql = context.ColumnMetadata
            .Where(x => dataSourceIds.Contains(x.DatabaseMetadata.DataSourceId))
            .Where(x => tableNames.Contains(x.DatabaseMetadata.TableName))
            .Where(x => columnNames.Contains(x.ColumnName))
            .Select(x =>
                new
                {
                    x.DatabaseMetadata.DataSourceId,
                    x.DatabaseMetadata.SchemaName,
                    x.DatabaseMetadata.TableName,
                    x.ColumnName,
                    x.Description
                })
            .ToQueryString();

        sql.Should().Contain("[ColumnName]");
        System.Text.RegularExpressions.Regex.Matches(sql, @"DECLARE @").Count.Should().BeLessThan(2100);
    }

    [Test]
    public void TableDescriptionBatch_SqlServer_Translates()
    {
        using var context = SqlServerTestContext.Create();
        int[] dataSourceIds = [1, 2];
        List<string?> tableNames = ["orders", "items"];

        var sql = context.DatabaseMetadata
            .Where(x => dataSourceIds.Contains(x.DataSourceId))
            .Where(x => tableNames.Contains(x.TableName))
            .Select(x =>
                new
                {
                    x.DataSourceId,
                    x.SchemaName,
                    x.TableName,
                    x.TableDescription
                })
            .ToQueryString();

        sql.Should().Contain("[TableDescription]");
    }
}
