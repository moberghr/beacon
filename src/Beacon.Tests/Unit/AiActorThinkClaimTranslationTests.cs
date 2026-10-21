using Beacon.AI.Services.Ai.AiActor;
using Beacon.Core.Data;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class AiActorThinkClaimTranslationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void ThinkClaimPredicate_Translates(bool sqlServer)
    {
        using var context = sqlServer ? (BeaconContext)SqlServerTestContext.Create() : NpgsqlTestContext.Create();

        var sql = EfAiActorThinkClaim.ClaimQuery(context, 7, new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc)).ToQueryString();

        var column = sqlServer ? "[LastThinkTime]" : "last_think_time";
        var archived = sqlServer ? "[ArchivedTime] IS NULL" : "archived_time IS NULL";
        sql.Should().Contain(archived, "the soft-delete filter applies to the claim");
        sql.Should().Contain($"{column} IS NULL OR");
        sql.Should().MatchRegex($@"{System.Text.RegularExpressions.Regex.Escape(column)} <= (@|\{{)", "the threshold is a parameter");
        sql.Should().Contain("2026-10-21");
    }
}
