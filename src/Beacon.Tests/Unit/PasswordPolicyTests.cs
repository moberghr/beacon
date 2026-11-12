using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The rules every new password meets: the minimum length (code-configured, raised by
/// <c>Beacon:UserManagement:MinimumPasswordLength</c>) on every path that sets one, and a changed password that differs
/// from the current one, judged against the stored hash.
/// </summary>
[TestFixture]
public class PasswordPolicyTests
{
    private const string Short = "Aa1!aaaaaa";
    private const string Long = "Aa1!aaaaaaaaaa";

    [Test]
    public async Task ChangePassword_ToTheCurrentPassword_IsRefused_AndNothingIsWritten()
    {
        var user = InternalUser();
        var hasher = Hasher();
        var service = Service([user], hasher, new BeaconConfiguration());

        var result = await service.ChangePasswordAsync(1, "Current-password-1", "Current-password-1");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("The new password must differ from the current one.");
        hasher.Verify(x => x.HashPassword(It.IsAny<string>()), Times.Never);
        user.PasswordHash.Should().Be("hash-a");
        user.SecurityStamp.Should().BeNull("a refused change signs nobody out");
    }

    [Test]
    public async Task ChangePassword_ToANewPassword_IsAccepted()
    {
        var user = InternalUser();
        var service = Service([user], Hasher(), new BeaconConfiguration());

        var result = await service.ChangePasswordAsync(1, "Current-password-1", "A-new-password-1234");

        result.Success.Should().BeTrue();
        user.PasswordHash.Should().Be("new-hash");
    }

    [Test]
    public async Task MinimumLength_HoldsOnEveryPathThatSetsAPassword()
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.MinimumPasswordLength = 12;
        var expected = "Password must be at least 12 characters long.";

        var superAdmin = async () => await Service([], Hasher(), configuration).CreateSuperAdminAsync(
            new CreateSuperAdminRequest { UserName = "root", Password = Short, ConfirmPassword = Short });
        var created = await Service([], Hasher(), configuration).CreateInternalUserAsync(
            new CreateInternalUserRequest { UserName = "ben", Password = Short });
        var changed = await Service([InternalUser()], Hasher(), configuration).ChangePasswordAsync(1, "Current-password-1", Short);
        var reset = await Service([InternalUser()], Hasher(), configuration).ResetPasswordAsync(1, Short);
        var mustChange = InternalUser();
        mustChange.MustChangePassword = true;
        var required = await Service([mustChange], Hasher(), configuration)
            .CompleteRequiredPasswordChangeAsync("ana", "Current-password-1", Short, CancellationToken.None);

        await superAdmin.Should().ThrowAsync<BeaconException>().WithMessage(expected);
        created.Message.Should().Be(expected);
        changed.Message.Should().Be(expected);
        reset.Message.Should().Be(expected);
        required.ErrorMessage.Should().Be(expected);

        (await Service([InternalUser()], Hasher(), configuration).ResetPasswordAsync(1, Long)).Success.Should().BeTrue();
    }

    [Test]
    public void ConfiguredMinimumLength_RaisesTheCodeConfiguredOne_AndNeverLowersIt()
    {
        var raised = new UserManagementOptions { MinimumPasswordLength = 8 };
        var kept = new UserManagementOptions { MinimumPasswordLength = 14 };
        var unset = new UserManagementOptions();

        raised.ApplyConfiguredMinimumPasswordLength(12);
        kept.ApplyConfiguredMinimumPasswordLength(10);
        unset.ApplyConfiguredMinimumPasswordLength(null);

        raised.MinimumPasswordLength.Should().Be(12);
        kept.MinimumPasswordLength.Should().Be(14);
        unset.MinimumPasswordLength.Should().Be(8, "the default is unchanged");
    }

    [TestCase(0)]
    [TestCase(200)]
    public void CodeConfiguredMinimumLength_WithoutAConfiguredOne_IsLeftAsTheHostSetIt(int codeConfigured)
    {
        var options = new UserManagementOptions { MinimumPasswordLength = codeConfigured };

        options.Invoking(x => x.ApplyConfiguredMinimumPasswordLength(null)).Should().NotThrow();

        options.MinimumPasswordLength.Should().Be(codeConfigured);
    }

    [Test]
    public void ConfiguredMinimumLength_RaisesACodeConfiguredZero()
    {
        var options = new UserManagementOptions { MinimumPasswordLength = 0 };

        options.ApplyConfiguredMinimumPasswordLength(10);

        options.MinimumPasswordLength.Should().Be(10);
    }

    [TestCase(0)]
    [TestCase(129)]
    public void ConfiguredMinimumLengthOutOfRange_FailsStartup(int configured)
    {
        var options = new UserManagementOptions();

        options.Invoking(x => x.ApplyConfiguredMinimumPasswordLength(configured))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*Beacon:UserManagement:MinimumPasswordLength*");
    }

    [Test]
    public void ConfiguredMinimumLength_IsBoundFromConfiguration()
    {
        var beacon = new BeaconConfiguration();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:EncryptionKey"] = TestEncryptionKey.Value,
                [UserManagementOptions.MinimumPasswordLengthKey] = "14"
            })
            .Build();

        new ServiceCollection().AddBeaconServices(configuration, x =>
        {
            x.AddBeaconScheduler<NoOpScheduler>();
            beacon = x;
        });

        beacon.UserManagement.MinimumPasswordLength.Should().Be(14);
    }

    private static Mock<IPasswordHasher> Hasher()
    {
        var hasher = new Mock<IPasswordHasher>();
        hasher
            .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string, string>((password, hash, _) => password == "Current-password-1" && hash == "hash-a");
        hasher.Setup(x => x.HashPassword(It.IsAny<string>())).Returns(("new-hash", "new-salt"));

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

    private static UserManagementService Service(List<BeaconUser> users, Mock<IPasswordHasher> hasher, BeaconConfiguration configuration)
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
            configuration,
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero)),
            new RecordingSecurityAudit(),
            rateLimits: Options.Create(new RateLimitOptions()));
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
