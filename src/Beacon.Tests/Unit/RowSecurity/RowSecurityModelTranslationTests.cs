using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.RowSecurity;

/// <summary>
/// The reads row security makes of its model, as PostgreSQL and SQL Server translate them without a database: a source's
/// live policies that have an active version, with that version's definition; a source's tables with a class in force; a
/// user's values, of live attributes only (an archived attribute holds no value for anyone); and a source's row-security
/// version.
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
            sql.Should().Contain("FROM [beacon].[RowPolicies] AS [r]")
                .And.Contain("JOIN [beacon].[RowPolicyVersions] AS [r0] ON [r].[ActiveVersionId] = [r0].[Id]")
                .And.Contain("[r].[ArchivedTime] IS NULL")
                .And.Contain("[r].[DataSourceId] = @dataSourceId")
                .And.Contain("[r].[ActiveVersionId] IS NOT NULL");
            return;
        }

        sql.Should().Contain("FROM beacon.row_policies AS r")
            .And.Contain("JOIN beacon.row_policy_versions AS r0 ON r.active_version_id = r0.id")
            .And.Contain("r.archived_time IS NULL")
            .And.Contain("r.data_source_id = @dataSourceId")
            .And.Contain("r.active_version_id IS NOT NULL");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ASourcesClassesInForce_AndItsVersion_Translate(bool sqlServer)
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
        var version = context.DataSources
            .Where(x => x.Id == dataSourceId)
            .Select(x => x.RowSecurityVersion)
            .ToQueryString();

        if (sqlServer)
        {
            classes.Should().Contain("SELECT [t].[SchemaName], [t].[TableName], [t].[Class]")
                .And.Contain("FROM [beacon].[TableClassifications] AS [t]")
                .And.Contain("[t].[DataSourceId] = @dataSourceId")
                .And.Contain("[t].[Class] IS NOT NULL");
            version.Should().Contain("SELECT [d].[RowSecurityVersion]").And.Contain("FROM [beacon].[DataSources] AS [d]");
            return;
        }

        classes.Should().Contain("SELECT t.schema_name AS \"SchemaName\", t.table_name AS \"TableName\", t.class AS \"Class\"")
            .And.Contain("FROM beacon.table_classifications AS t")
            .And.Contain("t.data_source_id = @dataSourceId")
            .And.Contain("t.class IS NOT NULL");
        version.Should().Contain("SELECT d.row_security_version").And.Contain("FROM beacon.data_sources AS d");
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
                .And.Contain("INNER JOIN (")
                .And.Contain("FROM [beacon].[AttributeDefinitions] AS [a]")
                .And.Contain("WHERE [a].[ArchivedTime] IS NULL")
                .And.Contain("[u].[UserId] = @userId");
            return;
        }

        sql.Should().Contain("FROM beacon.user_attribute_values AS u")
            .And.Contain("INNER JOIN (")
            .And.Contain("FROM beacon.attribute_definitions AS a")
            .And.Contain("WHERE a.archived_time IS NULL")
            .And.Contain("u.user_id = @userId");
    }

    private static BeaconContext Context(bool sqlServer)
    {
        return sqlServer ? SqlServerTestContext.Create() : NpgsqlTestContext.Create();
    }
}
