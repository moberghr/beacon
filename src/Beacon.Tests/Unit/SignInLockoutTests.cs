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
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The password lockout (Beacon:RateLimits:Lockout): after the configured number of wrong passwords an internal user is
/// refused for the configured time whatever password is sent, with the generic answer, and the password is still checked;
/// a success, the lockout itself and a password reset start the count again; the lock is a security audit event.
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
        RateLimitOptions? options = null)
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
            new BeaconConfiguration(),
            clock,
            audit,
            rateLimits: Options.Create(options ?? new RateLimitOptions()));
    }
}
