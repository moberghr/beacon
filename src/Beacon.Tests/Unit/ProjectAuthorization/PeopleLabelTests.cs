using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Tasks;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Query versions and tasks name the people behind them by display name, or "User {id}" without one — never
/// by user name or external id — and do not return the external id. Over in-memory sets (no database, §4.7).
/// </summary>
[TestFixture]
public class PeopleLabelTests
{
    private const string AuthorExternalId = "ext-ada";

    [TestCase(typeof(QueryVersionSummary), "CreatedByUserId")]
    [TestCase(typeof(QueryVersionDetail), "CreatedByUserId")]
    [TestCase(typeof(TaskDetailResult), "AssigneeUserId")]
    public void RestShapes_DoNotReturnAnExternalId(Type shape, string property)
    {
        shape.GetProperty(property).Should().BeNull($"{shape.Name} names people by label, not by their external id");
    }

    [TestCase("Ada Lovelace", "Ada Lovelace")]
    [TestCase(null, "User 3")]
    public async Task VersionDetail_NamesItsAuthorByDisplayNameOrUserId(string? displayName, string expected)
    {
        var service = VersionService([Author(displayName)]);

        var detail = await service.GetVersionDetailAsync(5);

        detail!.CreatedByUserName.Should().Be(expected);
    }

    [Test]
    public async Task VersionDetail_OfAnAuthorWhoIsNotABeaconUser_NamesNoOne_EvenWithAStoredUserName()
    {
        var service = VersionService([]);

        var detail = await service.GetVersionDetailAsync(5);

        detail!.CreatedByUserName.Should().BeNull("the user name stored with the version is not shown to other users");
    }

    private static QueryVersionService VersionService(List<BeaconUser> users)
    {
        var version = new QueryVersion
        {
            Id = 5,
            QueryId = 1,
            VersionNumber = 2,
            Name = "Orders",
            StepsJson = "[]",
            Status = QueryVersionStatus.Active,
            CreatedByUserId = AuthorExternalId,
            CreatedByUserName = "ada@corp.example"
        };

        return TestQueryServices.VersionService(Factory([version], users));
    }

    private static BeaconUser Author(string? displayName)
    {
        return new BeaconUser { Id = 3, ExternalId = AuthorExternalId, UserName = "ada@corp.example", DisplayName = displayName, IsEnabled = true };
    }

    private static IDbContextFactory<BeaconContext> Factory<T>(List<T> rows, List<BeaconUser> users) where T : class
    {
        var saved = new List<object>();
        var sets = new Dictionary<Type, object>
        {
            [typeof(T)] = RecordingBeaconContext.MemorySet(rows, saved),
            [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(users, saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));

        return factory.Object;
    }
}
