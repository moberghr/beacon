using Beacon.Core.Data;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Beacon.Tests.Integration;

/// <summary>
/// Query translation (§4.3), on both providers, of the query shapes the row-data minimisation added: the columns whose
/// sampled values an archive or a migration write target clears (archived tables of the source included) — the
/// production query, its SQL captured offline — and the stored MCP settings the startup warning reads for every deployment
/// lock, the row-data fields included. No database is reached.
/// </summary>
[TestFixture]
public class RowDataTranslationTests
{
    private const int DataSourceId = 5;

    [TestCase(false)]
    [TestCase(true)]
    public async Task TheColumnsAnArchiveOrAWriteTargetClears_Translates(bool sqlServer)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture()).ThenNoRows();
        await using var context = await capture.Factory().CreateDbContextAsync();

        await DatabaseMetadataService.ClearStoredSampleValuesAsync(context, DataSourceId, CancellationToken.None);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain(sqlServer ? "[DataSourceId] = @" : "data_source_id = @");
        sql.Should().Contain(sqlServer ? "[SampleValues] IS NOT NULL" : "sample_values IS NOT NULL");
        sql.Should().Contain(sqlServer ? "[SampleValuesComplete]" : "sample_values_complete");
        sql.Should().NotContain(sqlServer ? "[ArchivedTime]" : "archived_time", "an archived table of the source is cleared too");
        sql.Should().NotContain("WHERE FALSE").And.NotContain("0 = 1").And.NotContain("1 = 0");
        capture.CommandParameters.Single().Values.Should().Contain(DataSourceId);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TheStoredSettingsTheStartupWarningReads_Translates(bool sqlServer)
    {
        using var context = Context(sqlServer);

        var global = context.McpSettings
            .Select(x =>
                new
                {
                    x.Id,
                    x.EnforceReadOnly,
                    x.EnablePiiDetection,
                    x.RetainQueryContent,
                    x.EnableSampleValueCollection,
                    x.EnableValueGrounding,
                    x.EnableEvalJudge
                })
            .ToQueryString();
        var projects = context.McpProjectSettings
            .Select(x =>
                new
                {
                    x.ProjectId,
                    x.EnforceReadOnly,
                    x.EnablePiiDetection,
                    x.RetainQueryContent,
                    x.EnableSampleValueCollection,
                    x.EnableValueGrounding,
                    x.EnableEvalJudge
                })
            .ToQueryString();

        foreach (var sql in new[] { global, projects })
        {
            sql.Should().Contain(sqlServer ? "[EnableSampleValueCollection]" : "enable_sample_value_collection");
            sql.Should().Contain(sqlServer ? "[EnableValueGrounding]" : "enable_value_grounding");
            sql.Should().Contain(sqlServer ? "[EnableEvalJudge]" : "enable_eval_judge");
        }
    }

    private static BeaconContext Context(bool sqlServer)
    {
        return sqlServer ? SqlServerTestContext.Create() : NpgsqlTestContext.Create();
    }
}
