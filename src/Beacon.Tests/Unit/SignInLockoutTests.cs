using System.Collections;
using System.Linq.Expressions;
using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The password lockout (Beacon:RateLimits:Lockout): after the configured number of wrong passwords an internal user is
/// refused for the configured time whatever password is sent, with the generic answer, and the password is still checked;
/// a success, the lockout itself, a password reset and a lockout duration without wrong passwords start the count again;
/// the lock is a security audit event. Whether a right password signs in is decided after the password check by the
/// statement that clears the count, so an attempt racing a concurrent lock is refused. Break-glass accounts are never
/// locked, and a host can switch the lockout off.
/// </summary>
[TestFixture]
public class SignInLockoutTests
{
    private const string Generic = "Invalid username or password.";

    [Test]
    public async Task FifthWrongPassword_LocksTheUser_AndTheRightPasswordIsThenRefusedTheSameWay()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var user = InternalUser();
        var audit = new RecordingSecurityAudit();
        var hasher = Hasher();
        var service = Service([user], hasher, clock, audit);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            (await service.AuthenticateInternalUserAsync("ana", "wrong")).LockedOut.Should().BeFalse();
        }

        user.FailedSignInCount.Should().Be(4);
        user.LockoutEndTime.Should().BeNull();
        audit.Events.Should().NotContain(x => x.Action == SecurityAuditActions.UserLockedOut);

        var fifth = await service.AuthenticateInternalUserAsync("ana", "wrong");

        fifth.Success.Should().BeFalse();
        user.LockoutEndTime.Should().Be(clock.GetUtcNow().UtcDateTime.AddMinutes(15));
        user.FailedSignInCount.Should().Be(0, "the lockout starts the count again");
        audit.Events.Should().ContainSingle(x => x.Action == SecurityAuditActions.UserLockedOut)
            .Which.ResourceId.Should().Be("1");

        var rightWhileLocked = await service.AuthenticateInternalUserAsync("ana", "right");

        rightWhileLocked.Success.Should().BeFalse();
        rightWhileLocked.ErrorMessage.Should().Be(Generic, "a locked account answers like a wrong password");
        rightWhileLocked.LockedOut.Should().BeTrue();
        rightWhileLocked.User.Should().BeNull();
        hasher.Verify(x => x.VerifyPassword("right", "hash-a", "salt"), Times.Once, "the password is still checked");
        user.LastLoginAt.Should().BeNull();
    }

    [Test]
    public async Task WrongPasswordsWhileLocked_DoNotExtendTheLockout()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var lockedUntil = clock.GetUtcNow().UtcDateTime.AddMinutes(10);
        var user = InternalUser();
        user.LockoutEndTime = lockedUntil;
        var audit = new RecordingSecurityAudit();
        var service = Service([user], Hasher(), clock, audit);

        for (var attempt = 0; attempt < 6; attempt++)
        {
            (await service.AuthenticateInternalUserAsync("ana", "wrong")).LockedOut.Should().BeTrue();
        }

        user.LockoutEndTime.Should().Be(lockedUntil);
        user.FailedSignInCount.Should().Be(0);
        audit.Events.Should().NotContain(x => x.Action == SecurityAuditActions.UserLockedOut);
    }

    [Test]
    public async Task AfterTheLockoutEnds_TheRightPasswordSignsIn_AndClearsTheLockout()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var user = InternalUser();
        user.LockoutEndTime = clock.GetUtcNow().UtcDateTime.AddMinutes(15);
        var service = Service([user], Hasher(), clock, new RecordingSecurityAudit());

        clock.Advance(TimeSpan.FromMinutes(15));
        var result = await service.AuthenticateInternalUserAsync("ana", "right");

        result.Success.Should().BeTrue();
        user.LockoutEndTime.Should().BeNull();
        user.FailedSignInCount.Should().Be(0);
    }

    [Test]
    public async Task SuccessfulSignIn_StartsTheCountAgain()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var user = InternalUser();
        var service = Service([user], Hasher(), clock, new RecordingSecurityAudit());

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await service.AuthenticateInternalUserAsync("ana", "wrong");
        }

        (await service.AuthenticateInternalUserAsync("ana", "right")).Success.Should().BeTrue();
        user.FailedSignInCount.Should().Be(0);

        (await service.AuthenticateInternalUserAsync("ana", "wrong")).Success.Should().BeFalse();
        user.LockoutEndTime.Should().BeNull("one wrong password after a success is the first of a new count");
    }

    [Test]
    public async Task UnknownName_IsCountedAgainstNobody()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var user = InternalUser();
        var audit = new RecordingSecurityAudit();
        var service = Service([user], Hasher(), clock, audit);

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var result = await service.AuthenticateInternalUserAsync("nobody", "wrong");
            result.ErrorMessage.Should().Be(Generic);
            result.LockedOut.Should().BeFalse();
        }

        user.FailedSignInCount.Should().Be(0);
        audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task LockoutOff_NeverLocks()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var user = InternalUser();
        var options = new RateLimitOptions { Lockout = new LockoutOptions { MaxFailedAttempts = 0 } };
        var service = Service([user], Hasher(), clock, new RecordingSecurityAudit(), options);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await service.AuthenticateInternalUserAsync("ana", "wrong");
        }

        user.LockoutEndTime.Should().BeNull();
        (await service.AuthenticateInternalUserAsync("ana", "right")).Success.Should().BeTrue();
    }

    [Test]
    public async Task RequiredPasswordChange_IsRefusedWhileLocked()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var user = InternalUser();
        user.MustChangePassword = true;
        user.LockoutEndTime = clock.GetUtcNow().UtcDateTime.AddMinutes(5);
        var service = Service([user], Hasher(), clock, new RecordingSecurityAudit());

        var result = await service.CompleteRequiredPasswordChangeAsync("ana", "right", "A-new-password-1234", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(Generic);
        user.MustChangePassword.Should().BeTrue();
    }

    [Test]
    public async Task PasswordReset_ClearsTheLockout()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var user = InternalUser();
        user.FailedSignInCount = 3;
        user.LockoutEndTime = clock.GetUtcNow().UtcDateTime.AddMinutes(5);
        var hasher = Hasher();
        hasher.Setup(x => x.HashPassword(It.IsAny<string>())).Returns(("new-hash", "new-salt"));
        var service = Service([user], hasher, clock, new RecordingSecurityAudit());

        var result = await service.ResetPasswordAsync(1, "A-new-password-1234");

        result.Success.Should().BeTrue();
        user.LockoutEndTime.Should().BeNull();
        user.FailedSignInCount.Should().Be(0);
    }

    [Test]
    public async Task RightPassword_WhileAConcurrentAttemptLocksTheUser_IsRefusedTheSameWay()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var stored = InternalUser();
        stored.FailedSignInCount = 4;
        stored.LastFailedSignInTime = clock.GetUtcNow().UtcDateTime.AddMinutes(-1);
        var audit = new RecordingSecurityAudit();
        UserManagementService? service = null;
        var concurrentAttempts = 0;
        var hasher = new Mock<IPasswordHasher>();
        hasher
            .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string, string>((password, hash, _) =>
            {
                // While the right password is being checked, a concurrent attempt sends the fifth wrong one.
                if (password == "right" && concurrentAttempts++ == 0)
                {
                    service!.AuthenticateInternalUserAsync("ana", "wrong").GetAwaiter().GetResult().Success.Should().BeFalse();
                }

                return password == "right" && hash == "hash-a";
            });
        service = SnapshotService(stored, hasher, clock, audit);

        var result = await service.AuthenticateInternalUserAsync("ana", "right");

        stored.LockoutEndTime.Should().Be(clock.GetUtcNow().UtcDateTime.AddMinutes(15), "the concurrent attempt locked the user");
        result.Success.Should().BeFalse("the lock is decided after the password check, not from the user as loaded before it");
        result.ErrorMessage.Should().Be(Generic);
        result.LockedOut.Should().BeTrue();
        result.User.Should().BeNull();
        audit.Events.Should().ContainSingle(x => x.Action == SecurityAuditActions.UserLockedOut);
    }

    [Test]
    public async Task RightPassword_WhileAConcurrentAttemptLocksTheUser_DoesNotCompleteARequiredPasswordChange()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var stored = InternalUser();
        stored.MustChangePassword = true;
        stored.FailedSignInCount = 4;
        stored.LastFailedSignInTime = clock.GetUtcNow().UtcDateTime.AddMinutes(-1);
        UserManagementService? service = null;
        var concurrentAttempts = 0;
        var hasher = new Mock<IPasswordHasher>();
        hasher
            .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string, string>((password, hash, _) =>
            {
                if (password == "right" && concurrentAttempts++ == 0)
                {
                    service!.AuthenticateInternalUserAsync("ana", "wrong").GetAwaiter().GetResult();
                }

                return password == "right" && hash == "hash-a";
            });
        service = SnapshotService(stored, hasher, clock, new RecordingSecurityAudit());

        var result = await service.CompleteRequiredPasswordChangeAsync("ana", "right", "A-new-password-1234", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(Generic);
        hasher.Verify(x => x.HashPassword(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task BreakGlassAccount_IsNeverLockedOut()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var user = InternalUser();
        var audit = new RecordingSecurityAudit();
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.LocalLogin.Enabled = false;
        configuration.UserManagement.LocalLogin.BreakGlassUserNames = ["ana"];
        var service = Service([user], Hasher(), clock, audit, configuration: configuration);

        for (var attempt = 0; attempt < 12; attempt++)
        {
            var refused = await service.AuthenticateInternalUserAsync("ana", "wrong");
            refused.ErrorMessage.Should().Be(Generic);
            refused.LockedOut.Should().BeFalse();
        }

        user.FailedSignInCount.Should().Be(0, "a break-glass account is not counted");
        user.LockoutEndTime.Should().BeNull();
        audit.Events.Should().NotContain(x => x.Action == SecurityAuditActions.UserLockedOut);
        (await service.AuthenticateInternalUserAsync("ana", "right")).Success.Should().BeTrue();
    }

    [Test]
    public async Task BreakGlassAccount_WithAStoredLockout_StillSignsIn()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var user = InternalUser();
        user.LockoutEndTime = clock.GetUtcNow().UtcDateTime.AddMinutes(10);
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.LocalLogin.BreakGlassUserNames = ["ana"];
        var service = Service([user], Hasher(), clock, new RecordingSecurityAudit(), configuration: configuration);

        (await service.AuthenticateInternalUserAsync("ana", "right")).Success.Should().BeTrue();
    }

    [Test]
    public async Task LockoutSwitchedOff_NeverLocks_AndIgnoresAStoredLockout()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var user = InternalUser();
        var options = new RateLimitOptions { Lockout = new LockoutOptions { Enabled = false } };
        var service = Service([user], Hasher(), clock, new RecordingSecurityAudit(), options);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await service.AuthenticateInternalUserAsync("ana", "wrong");
        }

        user.FailedSignInCount.Should().Be(0);
        user.LockoutEndTime.Should().BeNull();
        user.LockoutEndTime = clock.GetUtcNow().UtcDateTime.AddMinutes(10);
        (await service.AuthenticateInternalUserAsync("ana", "right")).Success.Should().BeTrue();
    }

    [Test]
    public async Task WrongPasswords_ALockoutDurationApart_StartTheCountAgain()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var user = InternalUser();
        var service = Service([user], Hasher(), clock, new RecordingSecurityAudit());

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await service.AuthenticateInternalUserAsync("ana", "wrong");
        }

        clock.Advance(TimeSpan.FromMinutes(15));
        await service.AuthenticateInternalUserAsync("ana", "wrong");

        user.FailedSignInCount.Should().Be(1, "the previous wrong password is a lockout duration old");
        user.LockoutEndTime.Should().BeNull();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            clock.Advance(TimeSpan.FromMinutes(14));
            await service.AuthenticateInternalUserAsync("ana", "wrong");
        }

        user.FailedSignInCount.Should().Be(4, "each wrong password came within a lockout duration of the one before");
        clock.Advance(TimeSpan.FromMinutes(14));
        await service.AuthenticateInternalUserAsync("ana", "wrong");
        user.LockoutEndTime.Should().Be(clock.GetUtcNow().UtcDateTime.AddMinutes(15));
    }

    [Test]
    public async Task ChangePassword_WrongCurrentPasswords_LockTheUser_AndTheRightOneIsThenRefused()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var user = InternalUser();
        var hasher = Hasher();
        var service = Service([user], hasher, clock, new RecordingSecurityAudit());

        for (var attempt = 0; attempt < 5; attempt++)
        {
            (await service.ChangePasswordAsync(1, "wrong", "A-new-password-1234")).Success.Should().BeFalse();
        }

        user.LockoutEndTime.Should().Be(clock.GetUtcNow().UtcDateTime.AddMinutes(15));

        var whileLocked = await service.ChangePasswordAsync(1, "right", "A-new-password-1234");

        whileLocked.Success.Should().BeFalse();
        whileLocked.Message.Should().Be("Current password is incorrect.", "a locked user is answered like a wrong password");
        hasher.Verify(x => x.HashPassword(It.IsAny<string>()), Times.Never);
        user.PasswordHash.Should().Be("hash-a");
    }

    [Test]
    public async Task ChangePassword_RightCurrentPassword_StartsTheCountAgain()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var user = InternalUser();
        user.FailedSignInCount = 3;
        user.LastFailedSignInTime = clock.GetUtcNow().UtcDateTime.AddMinutes(-1);
        var hasher = Hasher();
        hasher.Setup(x => x.HashPassword(It.IsAny<string>())).Returns(("new-hash", "new-salt"));
        var service = Service([user], hasher, clock, new RecordingSecurityAudit());

        var result = await service.ChangePasswordAsync(1, "right", "A-new-password-1234");

        result.Success.Should().BeTrue();
        user.FailedSignInCount.Should().Be(0);
        user.LastFailedSignInTime.Should().BeNull();
    }

    [Test]
    public void Options_PartitionBoundOutOfRange_IsRefused()
    {
        var result = new RateLimitOptionsValidator().Validate(null, new RateLimitOptions { MaxPartitions = 10 });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain("MaxPartitions");
    }

    [Test]
    public void Options_OutOfRange_AreRefused()
    {
        var options = new RateLimitOptions
        {
            Mcp = new RateLimitWindow { PermitLimit = -1 },
            Ask = new RateLimitWindow { PermitLimit = 10, WindowSeconds = 0 },
            Lockout = new LockoutOptions { MaxFailedAttempts = 5, DurationMinutes = 0 }
        };

        var result = new RateLimitOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().HaveCount(3);
        new RateLimitOptionsValidator().Validate(null, new RateLimitOptions()).Succeeded.Should().BeTrue();
    }

    private static Mock<IPasswordHasher> Hasher()
    {
        var hasher = new Mock<IPasswordHasher>();
        hasher
            .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string, string>((password, hash, _) => password == "right" && hash == "hash-a");

        return hasher;
    }

    private static BeaconUser InternalUser()
    {
        return new BeaconUser
        {
            Id = 1,
            ExternalId = Guid.NewGuid().ToString(),
            UserName = "ana",
            IsInternalUser = true,
            IsEnabled = true,
            PasswordHash = "hash-a",
            PasswordSalt = "salt"
        };
    }

    private static UserManagementService Service(
        List<BeaconUser> users,
        Mock<IPasswordHasher> hasher,
        TimeProvider clock,
        RecordingSecurityAudit audit,
        RateLimitOptions? options = null,
        BeaconConfiguration? configuration = null)
    {
        var saved = new List<object>();
        var sets = new Dictionary<Type, object>
        {
            [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(users, saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));

        return new UserManagementService(
            factory.Object,
            hasher.Object,
            Mock.Of<IRoleService>(),
            configuration ?? new BeaconConfiguration(),
            clock,
            audit,
            rateLimits: Options.Create(options ?? new RateLimitOptions()));
    }

    // As a database serves concurrent attempts: every load returns a copy of the stored row as it is at that moment, and
    // the bulk updates write onto the stored row, so a copy loaded earlier does not see a later update.
    private static UserManagementService SnapshotService(BeaconUser stored, Mock<IPasswordHasher> hasher, TimeProvider clock, RecordingSecurityAudit audit)
    {
        var rows = new List<BeaconUser> { stored }.AsQueryable();
        var provider = new SnapshotReads(new TestAsyncQueryProvider<BeaconUser>(rows.Provider));
        var set = new Mock<DbSet<BeaconUser>>();
        set.As<IQueryable<BeaconUser>>().Setup(x => x.Provider).Returns(provider);
        set.As<IQueryable<BeaconUser>>().Setup(x => x.Expression).Returns(rows.Expression);
        set.As<IQueryable<BeaconUser>>().Setup(x => x.ElementType).Returns(typeof(BeaconUser));
        var sets = new Dictionary<Type, object> { [typeof(BeaconUser)] = set.Object };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, []));

        return new UserManagementService(
            factory.Object,
            hasher.Object,
            Mock.Of<IRoleService>(),
            new BeaconConfiguration(),
            clock,
            audit,
            rateLimits: Options.Create(new RateLimitOptions()));
    }

    private sealed class SnapshotReads(IAsyncQueryProvider database) : IAsyncQueryProvider
    {
        public IQueryable CreateQuery(Expression expression) => new SnapshotQuery<BeaconUser>(expression, this);

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new SnapshotQuery<TElement>(expression, this);

        public object? Execute(Expression expression) => database.Execute(expression);

        public TResult Execute<TResult>(Expression expression) => database.Execute<TResult>(expression);

        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
        {
            var result = database.ExecuteAsync<TResult>(expression, cancellationToken);
            if (result is Task<BeaconUser> read)
            {
                var row = read.GetAwaiter().GetResult();

                return (TResult)(object)Task.FromResult(row == null ? null! : Copy(row));
            }

            return result;
        }

        private static BeaconUser Copy(BeaconUser row)
        {
            return new BeaconUser
            {
                Id = row.Id,
                ExternalId = row.ExternalId,
                UserName = row.UserName,
                IsInternalUser = row.IsInternalUser,
                IsEnabled = row.IsEnabled,
                PasswordHash = row.PasswordHash,
                PasswordSalt = row.PasswordSalt,
                MustChangePassword = row.MustChangePassword,
                FailedSignInCount = row.FailedSignInCount,
                LockoutEndTime = row.LockoutEndTime,
                LastFailedSignInTime = row.LastFailedSignInTime
            };
        }
    }

    private sealed class SnapshotQuery<T>(Expression expression, IQueryProvider provider) : IOrderedQueryable<T>
    {
        public Type ElementType => typeof(T);

        public Expression Expression => expression;

        public IQueryProvider Provider => provider;

        public IEnumerator<T> GetEnumerator() => provider.Execute<IEnumerable<T>>(expression).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
