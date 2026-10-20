using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.RowSecurity;

/// <summary>
/// The reads row security makes of its model, as PostgreSQL and SQL Server translate them without a database: a source's
/// live policies that have an active version, with that version's definition; a source's tables with a class in force; and
/// a user's values read through their attribute, which the join to live attributes only keeps (an archived attribute grants
/// no row).
/// </summary>
[TestFixture]
public class RowSecurityModelTranslationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void ASourcesActivePolicies_WithTheirActiveDefinition_Translates(bool sqlServer)
    {
        using var context = Context(sqlServer);
        var dataSourceId = 7;

        var sql = context.RowPolicies
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.ActiveVersionId != null)
            .Select(x =>
                new
                {
                    x.Id,
                    x.SchemaName,
                    x.TableName,
                    x.ActiveVersion!.DefinitionJson
                })
            .ToQueryString();

        if (sqlServer)
        {
            sql.Should().Contain("[r0].[DefinitionJson]")
                .And.Contain("FROM [beacon].[RowPolicies] AS [r]")
                .And.Contain("LEFT JOIN [beacon].[RowPolicyVersions] AS [r0] ON [r].[ActiveVersionId] = [r0].[Id]")
                .And.Contain("[r].[ArchivedTime] IS NULL")
                .And.Contain("[r].[DataSourceId] = @dataSourceId")
                .And.Contain("[r].[ActiveVersionId] IS NOT NULL");
            return;
        }

        sql.Should().Contain("r0.definition_json")
            .And.Contain("FROM beacon.row_policies AS r")
            .And.Contain("LEFT JOIN beacon.row_policy_versions AS r0 ON r.active_version_id = r0.id")
            .And.Contain("r.archived_time IS NULL")
            .And.Contain("r.data_source_id = @dataSourceId")
            .And.Contain("r.active_version_id IS NOT NULL");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ASourcesClassesInForce_Translate(bool sqlServer)
    {
        using var context = Context(sqlServer);
        var dataSourceId = 7;

        var classes = context.TableClassifications
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.Class != null)
            .Select(x =>
                new
                {
                    x.SchemaName,
                    x.TableName,
                    x.Class
                })
            .ToQueryString();

        if (sqlServer)
        {
            classes.Should().Contain("SELECT [t].[SchemaName], [t].[TableName], [t].[Class]")
                .And.Contain("FROM [beacon].[TableClassifications] AS [t]")
                .And.Contain("WHERE [t].[DataSourceId] = @dataSourceId AND [t].[Class] IS NOT NULL");
            return;
        }

        classes.Should().Contain("t.schema_name").And.Contain("t.table_name").And.Contain("t.class")
            .And.Contain("FROM beacon.table_classifications AS t")
            .And.Contain("WHERE t.data_source_id = @dataSourceId AND t.class IS NOT NULL");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AUsersValues_OfLiveAttributesOnly_Translate(bool sqlServer)
    {
        using var context = Context(sqlServer);
        var userId = 12;

        var sql = context.UserAttributeValues
            .Where(x => x.UserId == userId)
            .Select(x =>
                new
                {
                    x.AttributeDefinition.Key,
                    x.AttributeDefinition.ValueType,
                    x.Value
                })
            .ToQueryString();

        if (sqlServer)
        {
            sql.Should().Contain("FROM [beacon].[UserAttributeValues] AS [u]")
                .And.MatchRegex(@"INNER JOIN \(\s*SELECT [^()]*\s+FROM \[beacon\]\.\[AttributeDefinitions\] AS \[a\]\s+WHERE \[a\]\.\[ArchivedTime\] IS NULL\s*\) AS \[a0\] ON \[u\]\.\[AttributeDefinitionId\] = \[a0\]\.\[Id\]")
                .And.Contain("WHERE [u].[UserId] = @userId");
            return;
        }

        sql.Should().Contain("FROM beacon.user_attribute_values AS u")
            .And.MatchRegex(@"INNER JOIN \(\s*SELECT [^()]*\s+FROM beacon\.attribute_definitions AS a\s+WHERE a\.archived_time IS NULL\s*\) AS a0 ON u\.attribute_definition_id = a0\.id")
            .And.Contain("WHERE u.user_id = @userId");
    }

    private static BeaconContext Context(bool sqlServer)
    {
        return sqlServer ? SqlServerTestContext.Create() : NpgsqlTestContext.Create();
    }
}
