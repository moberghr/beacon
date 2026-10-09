using System.Security.Cryptography;
using System.Text;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC11 — <see cref="ApiKeyService.ValidateApiKeyAsync"/> must reject the key of a disabled user (a disabled user
/// can no longer sign in, so their keys must stop working too). Keys with no linked user, and the revoked/expired
/// rules, keep their behaviour. The context's <c>ApiKeyCredentials</c> set is an async in-memory double (§4.7).
/// </summary>
[TestFixture]
public class ApiKeyServiceValidationTests
{
    private const string PlainTextKey = "sk-sem_testkeynotasecret0123456789";

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
        // The BeaconUser soft-delete filter makes Include(User) yield null while UserId is still set.
        var archived = Credential(user: null);
        archived.UserId = 42;
        var service = BuildService(archived);

        var credential = await service.ValidateApiKeyAsync(PlainTextKey);

        credential.Should().BeNull();
    }

    [Test]
    public async Task KeyOfEnabledUser_IsAccepted()
    {
        var service = BuildService(Credential(user: new BeaconUser { UserName = "alice", ExternalId = "ext-alice", IsEnabled = true }));

        var credential = await service.ValidateApiKeyAsync(PlainTextKey);

        credential.Should().NotBeNull();
        credential!.Name.Should().Be("test key");
        credential.User!.UserName.Should().Be("alice");
    }

    [Test]
    public async Task KeyWithNoLinkedUser_IsAccepted()
    {
        var service = BuildService(Credential(user: null));

        var credential = await service.ValidateApiKeyAsync(PlainTextKey);

        credential.Should().NotBeNull();
        credential!.Name.Should().Be("test key");
        credential.User.Should().BeNull();
    }

    [Test]
    public async Task RevokedKey_IsRejected()
    {
        var revoked = Credential(user: null);
        revoked.IsRevoked = true;
        var service = BuildService(revoked);

        var credential = await service.ValidateApiKeyAsync(PlainTextKey);

        credential.Should().BeNull();
    }

    [Test]
    public async Task ExpiredKey_IsRejected()
    {
        var expired = Credential(user: null);
        expired.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        var service = BuildService(expired);

        var credential = await service.ValidateApiKeyAsync(PlainTextKey);

        credential.Should().BeNull();
    }

    [Test]
    public async Task UnknownKey_IsRejected()
    {
        var service = BuildService(Credential(user: null));

        var credential = await service.ValidateApiKeyAsync("sk-sem_someotherkey");

        credential.Should().BeNull();
    }

    private static ApiKeyCredential Credential(BeaconUser? user) =>
        new()
        {
            Id = 7,
            UserId = user == null ? null : 42,
            Name = "test key",
            KeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PlainTextKey))).ToLowerInvariant(),
            KeyPrefix = PlainTextKey[..16],
            User = user
        };

    private static ApiKeyService BuildService(params ApiKeyCredential[] credentials)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApiKeyContext(credentials));

        return new ApiKeyService(factory.Object, NullLogger<ApiKeyService>.Instance);
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
