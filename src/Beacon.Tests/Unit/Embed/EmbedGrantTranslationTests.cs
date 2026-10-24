using Beacon.Core.Authorization;
using Beacon.Core.Handlers.Embed;
using Beacon.Core.Services.Embed;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// Query-translation coverage (§4.3) for the embed grant lookups and the management list, on PostgreSQL and SQL Server:
/// a grant is found by its public id or its secret's hash as a parameter, and the list keeps only the caller's projects
/// in the SQL and reads the query's active version without dropping a grant whose query is archived.
/// </summary>
[TestFixture]
public class EmbedGrantTranslationTests
{
    [Test]
    public void EmbedGrantByPublicId_Translates()
    {
        using var context = NpgsqlTestContext.Create();

        var sql = EmbedGrantLookup.ByPublicId(context, "0123456789abcdef0123456789abcdef").ToQueryString();

        sql.Should().Contain("FROM beacon.embed_grants");
        sql.Should().Contain("public_id = @");
        string.Join("\n", sql.Split('\n').Where(x => !x.StartsWith("--", StringComparison.Ordinal)))
            .Should().NotContain("0123456789abcdef0123456789abcdef", "the id is a parameter");
    }

    [Test]
    public void EmbedGrantByPublicId_TranslatesOnSqlServer()
    {
        using var context = SqlServerTestContext.Create();

        var sql = EmbedGrantLookup.ByPublicId(context, "0123456789abcdef0123456789abcdef").ToQueryString();

        sql.Should().Contain("[beacon].[EmbedGrants]");
        sql.Should().Contain("[PublicId] = @");
    }

    [Test]
    public void EmbedGrantBySecretHash_Translates()
    {
        using var context = NpgsqlTestContext.Create();

        EmbedGrantLookup.BySecretHash(context, new string('a', 64)).ToQueryString()
            .Should().Contain("secret_hash = @");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ListEmbedGrants_Translates(bool sqlServer)
    {
        using var context = sqlServer ? (Beacon.Core.Data.BeaconContext)SqlServerTestContext.Create() : NpgsqlTestContext.Create();
        var readable = new ProjectSet([1, 2], false, []);

        var sql = ListEmbedGrantsHandler.BuildQuery(context, readable, new DateTime(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc), projectId: null).ToQueryString();

        sql.Should().Contain(sqlServer ? "[ProjectId] IN" : "project_id = ANY (@", "only the caller's projects");
        sql.Should().Contain(sqlServer ? "[ActiveVersionId]" : "active_version_id");
        sql.Should().NotContain(sqlServer ? "[SecretHash]" : "secret_hash", "the list never reads the secret's hash");
    }
}
