using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Embed;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.SavedQueryTools;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using static Beacon.Tests.Unit.SavedQueryTools.SavedQueryTestData;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// The grant list keeps only the grants of projects the caller reads, names the creator by label, never returns a secret
/// or its hash, and gives each grant its status: revoked, expired, superseded (the query's active version moved on) or
/// active.
/// </summary>
[TestFixture]
public class ListEmbedGrantsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task TheList_KeepsTheReadableProjects_WithEachGrantsStatus()
    {
        var data = new SavedQueryTestData();
        data.Project(1, "A", data.DataSource(10, "warehouse"));
        data.Project(3, "C");
        var query = data.Query(5, null, [Step(1, 10, "SELECT 1")]);
        var version = data.Store.ListFor<QueryVersion>().Single();
        data.Store.ListFor<Beacon.Core.Data.Entities.BeaconUser>().Add(new Beacon.Core.Data.Entities.BeaconUser { Id = 3, ExternalId = "ext-ada", UserName = "ada", DisplayName = "Ada" });
        var active = Grant(1, 1, version, query);
        var revoked = Grant(2, 1, version, query, revokedTime: Now.UtcDateTime.AddHours(-1));
        var expired = Grant(3, 1, version, query, expiresTime: Now.UtcDateTime.AddSeconds(-1));
        var elsewhere = Grant(4, 3, version, query);
        data.Store.ListFor<EmbedGrant>().AddRange([active, revoked, expired, elsewhere]);

        var result = await Handler(data, TestProjectAccess.ReadableIn(1)).Handle(new ListEmbedGrantsQuery { Sort = "id" }, CancellationToken.None);

        result.Items.Select(x => (x.Id, x.Status)).Should().Equal(
            (1, EmbedGrantStatuses.Active),
            (2, EmbedGrantStatuses.Revoked),
            (3, EmbedGrantStatuses.Expired));
        result.TotalCount.Should().Be(3);
        var first = result.Items[0];
        first.CreatedByLabel.Should().Be("Ada");
        first.ProjectName.Should().Be("A");
        first.QueryName.Should().Be("Query 5");
        first.VersionNumber.Should().Be(1);
        first.AllowedOrigins.Should().Equal("https://portal.example.com");
        first.LockedParameters.Should().Equal("branch");
        typeof(EmbedGrantListItem).GetProperties().Select(x => x.Name).Should().NotContain(x => x.Contains("Secret"));
    }

    [Test]
    public async Task AGrantWhoseQueryHasANewerActiveVersion_IsSuperseded()
    {
        var data = new SavedQueryTestData();
        data.Project(1, "A", data.DataSource(10, "warehouse"));
        var query = data.Query(5, null, [Step(1, 10, "SELECT 1")]);
        var pinned = data.Store.ListFor<QueryVersion>().Single();
        var newer = data.AddVersion(query, 2, QueryVersionStatus.Active, [Step(1, 10, "SELECT 2")], approval: ApprovalStatus.Approved);
        query.ActiveVersionId = newer.Id;
        query.ActiveVersion = newer;
        data.Store.ListFor<EmbedGrant>().Add(Grant(1, 1, pinned, query));

        var result = await Handler(data, TestProjectAccess.ReadableIn(1)).Handle(new ListEmbedGrantsQuery(), CancellationToken.None);

        result.Items.Should().ContainSingle().Which.Status.Should().Be(EmbedGrantStatuses.Superseded);
    }

    private static ListEmbedGrantsHandler Handler(SavedQueryTestData data, Beacon.Core.Authorization.ProjectAccessScope scope) =>
        new(data.Store.Factory().Object, TestProjectAccess.Evaluator(scope), new FakeTimeProvider(Now));

    private static EmbedGrant Grant(int id, int projectId, QueryVersion version, Query query, DateTime? revokedTime = null, DateTime? expiresTime = null) =>
        new()
        {
            Id = id,
            PublicId = id.ToString("x32"),
            ProjectId = projectId,
            QueryId = query.Id,
            Query = query,
            QueryVersionId = version.Id,
            QueryVersion = version,
            Name = $"grant {id}",
            AllowedOriginsJson = "[\"https://portal.example.com\"]",
            FixedParametersJson = "{}",
            LockedParameterNamesJson = "[\"branch\"]",
            SecretHash = new string('a', 64),
            ExpiresTime = expiresTime ?? Now.UtcDateTime.AddDays(30),
            RevokedTime = revokedTime,
            CreatedByUserId = "ext-ada",
            CreatedTime = Now.UtcDateTime.AddDays(-id)
        };
}
