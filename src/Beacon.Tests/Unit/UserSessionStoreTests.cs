using Beacon.Core.Authentication;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The user store behind sign-in sessions, run as real store code without a database: the user a session names is read
/// with archived and disabled users included (they count as inactive, not as missing) and with the roles they hold now,
/// and a new security stamp is written to that one user only. The SQL is captured on both providers
/// (<see cref="SqlCapture"/>): the soft-delete filter is bypassed on the read, and the stamp is one UPDATE.
/// </summary>
[TestFixture]
public class UserSessionStoreTests
{
    [Test]
    public async Task Find_ReadsTheUsersStateStampAndRoles_ArchivedAndDisabledUsersIncluded()
    {
        var viewer = new BeaconRole { Id = 3, Name = "Viewer", Level = 1 };
        var users = new List<BeaconUser>
        {
            User(1, "ana", stamp: "s-1", roles: [viewer]),
            User(2, "ben", enabled: false),
            User(3, "cid", archived: true)
        };
        var store = new UserSessionStore(Factory(users), new RecordingSecurityAudit());

        var ana = await store.FindAsync("ana", CancellationToken.None);
        var ben = await store.FindAsync("ben", CancellationToken.None);
        var cid = await store.FindAsync("cid", CancellationToken.None);
        var nobody = await store.FindAsync("dan", CancellationToken.None);

        ana.Should().BeEquivalentTo(new { Id = 1, IsActive = true, SecurityStamp = "s-1", RoleNames = new[] { "Viewer" } });
        ben!.IsActive.Should().BeFalse();
        cid!.IsActive.Should().BeFalse("an archived user is refused, not mistaken for a missing one");
        nobody.Should().BeNull();
    }

    [Test]
    public async Task RenewStamp_ChangesThatUsersStampOnly()
    {
        var users = new List<BeaconUser> { User(1, "ana", stamp: "s-1"), User(2, "ben", stamp: "s-2") };
        var store = new UserSessionStore(Factory(users), new RecordingSecurityAudit());

        var renewed = await store.RenewStampAsync(1, CancellationToken.None);
        var unknown = await store.RenewStampAsync(9, CancellationToken.None);

        renewed.Should().BeTrue();
        unknown.Should().BeFalse();
        users[0].SecurityStamp.Should().MatchRegex("^[0-9a-f]{32}$");
        users[1].SecurityStamp.Should().Be("s-2");
    }

    [Test]
    public async Task Find_Translates()
    {
        var capture = new SqlCapture().ThenNoRows();

        await new UserSessionStore(capture.Factory(), new RecordingSecurityAudit()).FindAsync("ana", CancellationToken.None);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("FROM beacon.users");
        sql.Should().Contain("security_stamp");
        sql.Should().Contain("external_id = @");
        sql.Should().Contain("archived_time IS NULL", "the user is active only while not archived").And.Contain("is_enabled");
        sql.Should().NotContain("WHERE u.archived_time IS NULL", "the soft-delete filter is bypassed");
    }

    [Test]
    public async Task Find_Translates_OnSqlServer()
    {
        var capture = SqlCapture.OnSqlServer().ThenNoRows();

        await new UserSessionStore(capture.Factory(), new RecordingSecurityAudit()).FindAsync("ana", CancellationToken.None);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("[beacon].[Users]");
        sql.Should().Contain("[SecurityStamp]");
        sql.Should().Contain("[ExternalId] = @");
        sql.Should().NotContain("WHERE [u].[ArchivedTime] IS NULL", "the soft-delete filter is bypassed");
    }

    [Test]
    public async Task RenewStamp_Translates()
    {
        var capture = new SqlCapture().ThenRowsAffected(1);

        var renewed = await new UserSessionStore(capture.Factory(), new RecordingSecurityAudit()).RenewStampAsync(7, CancellationToken.None);

        renewed.Should().BeTrue();
        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().StartWith("UPDATE beacon.users");
        sql.Should().Contain("SET security_stamp = @");
        sql.Should().Contain("id = @");
        sql.Should().NotContain("archived_time IS NULL");
    }

    [Test]
    public async Task RenewStamp_Translates_OnSqlServer()
    {
        var capture = SqlCapture.OnSqlServer().ThenRowsAffected(1);

        var renewed = await new UserSessionStore(capture.Factory(), new RecordingSecurityAudit()).RenewStampAsync(7, CancellationToken.None);

        renewed.Should().BeTrue();
        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("UPDATE [u]");
        sql.Should().Contain("SET [u].[SecurityStamp] = @");
        sql.Should().NotContain("[ArchivedTime] IS NULL");
    }

    private static BeaconUser User(int id, string externalId, string? stamp = null, bool enabled = true, bool archived = false, List<BeaconRole>? roles = null)
    {
        var user = new BeaconUser
        {
            Id = id,
            ExternalId = externalId,
            UserName = externalId,
            IsEnabled = enabled,
            SecurityStamp = stamp,
            UserRoles = (roles ?? [])
                .Select(x => new BeaconUserRole { UserId = id, RoleId = x.Id, Role = x })
                .ToList()
        };
        if (archived)
        {
            user.Archive();
        }

        return user;
    }

    private static IDbContextFactory<BeaconContext> Factory(List<BeaconUser> users)
    {
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object> { [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(users, saved) },
                saved));

        return factory.Object;
    }
}
