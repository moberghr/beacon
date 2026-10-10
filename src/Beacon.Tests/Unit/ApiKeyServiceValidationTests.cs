using System.Security.Cryptography;
using System.Text;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// <see cref="ApiKeyService.ValidateApiKeyAsync"/> accepts a key only while its owner can sign in: a key with no owner,
/// or whose owner is missing, archived or disabled, is rejected, as are revoked and expired keys (expired at the expiry
/// instant). Each refusal is logged with the key id and the reason, never the key. The context's
/// <c>ApiKeyCredentials</c> set is an async in-memory double (§4.7); the real query's SQL through SqlCapture.
/// </summary>
[TestFixture]
public class ApiKeyServiceValidationTests
{
    private const string PlainTextKey = "sk-sem_testkeynotasecret0123456789";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task KeyOfDisabledUser_IsRejected()
    {
        var service = BuildService(Credential(user: new BeaconUser { UserName = "bob", ExternalId = "ext-bob", IsEnabled = false }));

        var credential = await service.ValidateApiKeyAsync(PlainTextKey);

        credential.Should().BeNull();
    }

    [Test]
    public async Task KeyOfArchivedUser_IsRejected()
    {
        // The owner is loaded past the soft-delete filter, so an archived owner is seen and refused.
        var archived = new BeaconUser { UserName = "carol", ExternalId = "ext-carol", IsEnabled = true };
        archived.Archive();
        var service = BuildService(Credential(user: archived));

        var credential = await service.ValidateApiKeyAsync(PlainTextKey);

        credential.Should().BeNull();
    }

    [Test]
    public async Task KeyWhoseOwnerIsMissing_IsRejected()
    {
        var orphaned = Credential(user: null);
        orphaned.UserId = 42;
        var service = BuildService(orphaned);

        var credential = await service.ValidateApiKeyAsync(PlainTextKey);

        credential.Should().BeNull();
    }

    [Test]
    public async Task KeyWithNoOwner_IsRejected()
    {
        var service = BuildService(Credential(user: null));

        var credential = await service.ValidateApiKeyAsync(PlainTextKey);

        credential.Should().BeNull("a key that belongs to no one follows no one's lifecycle");
    }

    [Test]
    public async Task KeyOfEnabledUser_IsAccepted()
    {
        var service = BuildService(Credential(user: EnabledUser()));

        var credential = await service.ValidateApiKeyAsync(PlainTextKey);

        credential.Should().NotBeNull();
        credential!.Name.Should().Be("test key");
        credential.User!.UserName.Should().Be("alice");
    }

    [Test]
    public async Task RevokedKey_IsRejected()
    {
        var revoked = Credential(user: EnabledUser());
        revoked.IsRevoked = true;
        var service = BuildService(revoked);

        var credential = await service.ValidateApiKeyAsync(PlainTextKey);

        credential.Should().BeNull();
    }

    [Test]
    public async Task ExpiredKey_IsRejected()
    {
        var expired = Credential(user: EnabledUser());
        expired.ExpiresAt = Now.UtcDateTime.AddSeconds(-1);
        var service = BuildService(expired);

        var credential = await service.ValidateApiKeyAsync(PlainTextKey);

        credential.Should().BeNull();
    }

    [Test]
    public async Task KeyExpiringExactlyNow_IsRejected_AndOneExpiringASecondLater_IsAccepted()
    {
        var atNow = Credential(user: EnabledUser());
        atNow.ExpiresAt = Now.UtcDateTime;
        var inOneSecond = Credential(user: EnabledUser());
        inOneSecond.ExpiresAt = Now.UtcDateTime.AddSeconds(1);

        var refused = await BuildService(atNow).ValidateApiKeyAsync(PlainTextKey);
        var accepted = await BuildService(inOneSecond).ValidateApiKeyAsync(PlainTextKey);

        refused.Should().BeNull("a key stops working at its expiry instant");
        accepted.Should().NotBeNull();
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task KeyWithoutExpiry_IsAcceptedUntilTheMaximumIsEnforcedOnExistingKeys(bool enforce, bool accepted)
    {
        var legacy = Credential(user: EnabledUser());
        legacy.CreatedTime = Now.UtcDateTime.AddDays(-31);
        var service = BuildService(new ApiKeyOptions { MaxLifetimeDays = 30, EnforceMaxLifetimeOnExistingKeys = enforce }, null, legacy);

        var credential = await service.ValidateApiKeyAsync(PlainTextKey);

        (credential != null).Should().Be(accepted);
    }

    [Test]
    public async Task KeyWithoutExpiry_WithinTheEnforcedMaximum_IsAccepted()
    {
        var legacy = Credential(user: EnabledUser());
        legacy.CreatedTime = Now.UtcDateTime.AddDays(-29);
        var service = BuildService(new ApiKeyOptions { MaxLifetimeDays = 30, EnforceMaxLifetimeOnExistingKeys = true }, null, legacy);

        var credential = await service.ValidateApiKeyAsync(PlainTextKey);

        credential.Should().NotBeNull();
    }

    [Test]
    public async Task KeyWithoutExpiry_IsWarnedAboutOncePerKey()
    {
        var legacy = Credential(user: EnabledUser());
        legacy.Id = 7001;
        var logs = new LogRecorder();
        var service = BuildService(new ApiKeyOptions(), logs, legacy);

        await service.ValidateApiKeyAsync(PlainTextKey);
        await service.ValidateApiKeyAsync(PlainTextKey);

        logs.Entries.Where(x => x.Level == LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().StartWith("API key 7001 does not expire");
    }

    [TestCase("revoked")]
    [TestCase("expired")]
    [TestCase("owner_disabled")]
    [TestCase("owner_archived")]
    [TestCase("owner_missing")]
    public async Task Refusal_IsLoggedWithTheKeyIdAndTheReason_NeverTheKey(string reason)
    {
        var refused = Credential(user: EnabledUser());
        refused.ExpiresAt = Now.UtcDateTime.AddDays(1);
        switch (reason)
        {
            case "revoked":
                refused.IsRevoked = true;
                break;
            case "expired":
                refused.ExpiresAt = Now.UtcDateTime.AddDays(-1);
                break;
            case "owner_disabled":
                refused.User!.IsEnabled = false;
                break;
            case "owner_archived":
                refused.User!.Archive();
                break;
            default:
                refused.User = null;
                break;
        }

        var logs = new LogRecorder();
        var credential = await BuildService(new ApiKeyOptions(), logs, refused).ValidateApiKeyAsync(PlainTextKey);

        credential.Should().BeNull();
        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning)
            .Which.Message.Should().Be($"API key 7 refused: {reason}");
        logs.Contains(PlainTextKey).Should().BeFalse();
        logs.Contains(refused.KeyHash).Should().BeFalse();
    }

    [Test]
    public async Task UnknownKey_IsRejected()
    {
        var service = BuildService(Credential(user: EnabledUser()));

        var credential = await service.ValidateApiKeyAsync("sk-sem_someotherkey");

        credential.Should().BeNull();
    }

    [Test]
    public async Task ApiKeyValidationQuery_JoinsTheOwnerAndRolesWithoutTheSoftDeleteFilter_Translates()
    {
        // The query ValidateApiKeyAsync runs, against the Npgsql provider without a database: the owner join must not
        // drop archived users (an archived owner would read as "no owner" instead of being refused), and the owner's
        // roles come in the same query.
        var capture = new SqlCapture().ThenNoRows();
        var service = new ApiKeyService(capture.Factory(), Options.Create(new ApiKeyOptions()), new FakeTimeProvider(Now), NullLogger<ApiKeyService>.Instance);

        var credential = await service.ValidateApiKeyAsync(PlainTextKey);

        credential.Should().BeNull();
        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("FROM beacon.api_key_credentials");
        sql.Should().Contain("LEFT JOIN beacon.users");
        sql.Should().Contain("beacon.user_roles");
        sql.Should().Contain("beacon.roles");
        sql.Should().Contain("key_hash");
        sql.Should().NotContain("archived_time IS NULL");
    }

    private static BeaconUser EnabledUser() => new() { UserName = "alice", ExternalId = "ext-alice", IsEnabled = true };

    private static ApiKeyCredential Credential(BeaconUser? user) =>
        new()
        {
            Id = 7,
            CreatedTime = Now.UtcDateTime.AddDays(-1),
            UserId = user == null ? null : 42,
            Name = "test key",
            KeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PlainTextKey))).ToLowerInvariant(),
            KeyPrefix = PlainTextKey[..16],
            User = user
        };

    private static ApiKeyService BuildService(params ApiKeyCredential[] credentials) =>
        BuildService(new ApiKeyOptions(), null, credentials);

    private static ApiKeyService BuildService(ApiKeyOptions options, LogRecorder? logs, params ApiKeyCredential[] credentials)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApiKeyContext(credentials));

        return new ApiKeyService(
            factory.Object,
            Options.Create(options),
            new FakeTimeProvider(Now),
            logs?.For<ApiKeyService>() ?? NullLogger<ApiKeyService>.Instance);
    }

    /// <summary>A BeaconContext whose <c>ApiKeyCredentials</c> set is an in-memory async sequence — no DB round-trip.</summary>
    private sealed class ApiKeyContext(IReadOnlyList<ApiKeyCredential> credentials) : BeaconContext(Options, "beacon")
    {
        private static readonly DbContextOptions<ApiKeyContext> Options =
            new DbContextOptionsBuilder<ApiKeyContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(ApiKeyCredential))
            {
                return (DbSet<TEntity>)(object)BuildSet(credentials);
            }

            return base.Set<TEntity>();
        }
    }

    private static DbSet<ApiKeyCredential> BuildSet(IReadOnlyList<ApiKeyCredential> credentials)
    {
        var data = credentials.AsQueryable();
        var set = new Mock<DbSet<ApiKeyCredential>>();
        set.As<IAsyncEnumerable<ApiKeyCredential>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<ApiKeyCredential>(data.GetEnumerator()));
        set.As<IQueryable<ApiKeyCredential>>()
            .Setup(x => x.Provider)
            .Returns(new TestAsyncQueryProvider<ApiKeyCredential>(data.Provider));
        set.As<IQueryable<ApiKeyCredential>>().Setup(x => x.Expression).Returns(data.Expression);
        set.As<IQueryable<ApiKeyCredential>>().Setup(x => x.ElementType).Returns(data.ElementType);
        set.As<IQueryable<ApiKeyCredential>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());

        return set.Object;
    }
}
