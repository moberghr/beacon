using System.Security.Claims;
using System.Text.Json;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.ApiKeys;
using Beacon.Core.Mcp;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.PostgreSql;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Worker;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// API-key issuance: the scope vocabulary (Read, Execute; the retired Admin scope is refused), the mandatory expiry
/// (90-day default, configurable maximum, its boundaries and binding), and who may issue what — Viewers Read keys only,
/// writers Execute keys (by the owner's record and the authorization provider), disabled users and any caller carrying
/// an <c>auth_method</c> (key, MCP caller, bearer token) nothing. No database: the context's set is an async double (§4.7).
/// </summary>
[TestFixture]
public class ApiKeyIssuanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task KeyRequestedWithoutExpiry_ExpiresAfterNinetyDays()
    {
        var (service, added) = BuildService();

        var (credential, _) = await service.GenerateApiKeyAsync(7, "ci", ["Read"]);

        credential.ExpiresAt.Should().Be(Now.UtcDateTime.AddDays(90));
        added.Should().ContainSingle().Which.ExpiresAt.Should().Be(Now.UtcDateTime.AddDays(90));
    }

    [Test]
    public async Task DefaultLifetime_IsCappedAtAShorterConfiguredMaximum()
    {
        var (service, _) = BuildService(maxLifetimeDays: 30);

        var (credential, _) = await service.GenerateApiKeyAsync(7, "ci", ["Read"]);

        credential.ExpiresAt.Should().Be(Now.UtcDateTime.AddDays(30));
    }

    [Test]
    public async Task ExpiryWithinTheMaximum_IsKept_AsUtc()
    {
        var (service, _) = BuildService();
        var requested = DateTime.SpecifyKind(Now.UtcDateTime.AddDays(365), DateTimeKind.Unspecified);

        var (credential, _) = await service.GenerateApiKeyAsync(7, "ci", ["Read"], expiresAt: requested);

        credential.ExpiresAt.Should().Be(Now.UtcDateTime.AddDays(365));
        credential.ExpiresAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [TestCase(366, 365)]
    [TestCase(31, 30)]
    public async Task ExpiryBeyondTheMaximum_IsRefused(int days, int maxLifetimeDays)
    {
        var (service, added) = BuildService(maxLifetimeDays);

        var act = () => service.GenerateApiKeyAsync(7, "ci", ["Read"], expiresAt: Now.UtcDateTime.AddDays(days));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*at most {maxLifetimeDays} days*");
        added.Should().BeEmpty();
    }

    [Test]
    public async Task ExpiryInThePast_IsRefused()
    {
        var (service, added) = BuildService();

        var act = () => service.GenerateApiKeyAsync(7, "ci", ["Read"], expiresAt: Now.UtcDateTime.AddMinutes(-1));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*future*");
        added.Should().BeEmpty();
    }

    [Test]
    public async Task ExpiryExactlyAtTheMaximum_IsAccepted_AndOneSecondLater_IsRefused()
    {
        var (service, added) = BuildService(maxLifetimeDays: 30);
        var maximum = Now.UtcDateTime.AddDays(30);

        var (atMaximum, _) = await service.GenerateApiKeyAsync(7, "ci", ["Read"], expiresAt: maximum);
        var beyond = () => service.GenerateApiKeyAsync(7, "ci", ["Read"], expiresAt: maximum.AddSeconds(1));

        atMaximum.ExpiresAt.Should().Be(maximum);
        await beyond.Should().ThrowAsync<InvalidOperationException>().WithMessage("*at most 30 days*");
        added.Should().ContainSingle();
    }

    [Test]
    public async Task ExpiryExactlyNow_IsRefused_AndOneSecondLater_IsAccepted()
    {
        var (service, added) = BuildService();

        var atNow = () => service.GenerateApiKeyAsync(7, "ci", ["Read"], expiresAt: Now.UtcDateTime);
        var (inOneSecond, _) = await service.GenerateApiKeyAsync(7, "ci", ["Read"], expiresAt: Now.UtcDateTime.AddSeconds(1));

        await atNow.Should().ThrowAsync<InvalidOperationException>().WithMessage("*future*");
        inOneSecond.ExpiresAt.Should().Be(Now.UtcDateTime.AddSeconds(1));
        added.Should().ContainSingle();
    }

    [Test]
    public async Task LocalExpiry_IsStoredAsTheSameInstantInUtc()
    {
        var (service, _) = BuildService();
        var instant = Now.UtcDateTime.AddDays(30);

        var (credential, _) = await service.GenerateApiKeyAsync(7, "ci", ["Read"], expiresAt: instant.ToLocalTime());

        credential.ExpiresAt.Should().Be(instant);
        credential.ExpiresAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [TestCase(new[] { "Read" }, new[] { "Read" })]
    [TestCase(new[] { "Execute" }, new[] { "Execute" })]
    [TestCase(new[] { "read", "EXECUTE", "Read" }, new[] { "Read", "Execute" })]
    [TestCase(new[] { " Execute " }, new[] { "Execute" })]
    public async Task ValidScopes_AreStoredCanonical(string[] requested, string[] stored)
    {
        var (service, added) = BuildService();

        await service.GenerateApiKeyAsync(7, "ci", requested);

        JsonSerializer.Deserialize<string[]>(added.Single().Scopes!).Should().Equal(stored);
    }

    [TestCase(new[] { "Admin" }, "*Admin scope can no longer be issued*")]
    [TestCase(new[] { "Read", "admin" }, "*Admin scope can no longer be issued*")]
    [TestCase(new[] { "Write" }, "*Unknown API key scope*")]
    [TestCase(new[] { "1" }, "*Unknown API key scope*")]
    [TestCase(new[] { "" }, "*Unknown API key scope*")]
    [TestCase(new string[0], "*at least one scope*")]
    public async Task InvalidScopes_AreRefused(string[] requested, string message)
    {
        var (service, added) = BuildService();

        var act = () => service.GenerateApiKeyAsync(7, "ci", requested);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        added.Should().BeEmpty();
    }

    [Test]
    public async Task NullScopes_AreRefused()
    {
        var (service, _) = BuildService();

        var act = () => service.GenerateApiKeyAsync(7, "ci", scopes: null!);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*at least one scope*");
    }

    // ── Who may issue what (CreateApiKeyHandler) ─────────────────────────────

    [TestCase(RoleService.RoleLevels.Viewer, "Read", true)]
    [TestCase(RoleService.RoleLevels.Viewer, "Execute", false)]
    [TestCase(RoleService.RoleLevels.Editor, "Read", true)]
    [TestCase(RoleService.RoleLevels.Editor, "Execute", true)]
    [TestCase(RoleService.RoleLevels.Admin, "Execute", true)]
    public async Task IssuanceMatrix_ExecuteNeedsWritePermission(int roleLevel, string scope, bool issued)
    {
        var apiKeys = ApiKeyServiceMock();
        var handler = BuildHandler(apiKeys, roleLevel: roleLevel);

        var act = () => handler.Handle(new CreateApiKeyCommand("ci", [scope], null, null), CancellationToken.None);

        if (issued)
        {
            (await act()).PlainTextKey.Should().Be("sk-sem_issued");
            apiKeys.Verify(x => x.GenerateApiKeyAsync(3, "ci", It.Is<string[]>(y => y.SequenceEqual(new[] { scope })), null, null, 0, It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*write permission*");
            apiKeys.VerifyNoOtherCalls();
        }
    }

    [Test]
    public async Task SuperAdminWithoutRoles_MayIssueExecuteKeys()
    {
        var apiKeys = ApiKeyServiceMock();
        var handler = BuildHandler(apiKeys, roleLevel: null, isSuperAdmin: true);

        await handler.Handle(new CreateApiKeyCommand("ci", ["Execute"], null, null), CancellationToken.None);

        apiKeys.Verify(x => x.GenerateApiKeyAsync(3, "ci", It.IsAny<string[]>(), null, null, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ExecuteKey_IsRefused_WhenTheProviderDeniesWrite_EvenToAnEditor()
    {
        var apiKeys = ApiKeyServiceMock();
        var handler = BuildHandler(apiKeys, roleLevel: RoleService.RoleLevels.Editor, providerCanWrite: false);

        var act = () => handler.Handle(new CreateApiKeyCommand("ci", ["Execute"], null, null), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*write permission*");
        apiKeys.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ExecuteKey_IsRefused_WhenTheOwnersRecordHasNoWritePermission_WhateverTheProviderSays()
    {
        var apiKeys = ApiKeyServiceMock();
        var handler = BuildHandler(apiKeys, roleLevel: RoleService.RoleLevels.Viewer, providerCanWrite: true);

        var act = () => handler.Handle(new CreateApiKeyCommand("ci", ["Execute"], null, null), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*write permission*");
        apiKeys.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Owner_IsResolvedFromNameIdentifier_NotFromTheBeaconUserIdClaim()
    {
        var apiKeys = ApiKeyServiceMock();
        var users = new Mock<IUserManagementService>(MockBehavior.Strict);
        users
            .Setup(x => x.GetUserByExternalIdAsync("ext-ada", It.IsAny<CancellationToken>()))
            .ReturnsAsync(User(RoleService.RoleLevels.Editor));
        var principal = SessionPrincipal(new Claim(BeaconClaims.UserId, "ext-bob"));
        var handler = new CreateApiKeyHandler(apiKeys.Object, Accessor(principal), users.Object, Authorization(canWrite: true));

        await handler.Handle(new CreateApiKeyCommand("ci", ["Execute"], null, null), CancellationToken.None);

        users.Verify(x => x.GetUserByExternalIdAsync("ext-ada", It.IsAny<CancellationToken>()), Times.Once);
        users.VerifyNoOtherCalls();
        apiKeys.Verify(x => x.GenerateApiKeyAsync(3, "ci", It.IsAny<string[]>(), null, null, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Handler_ForwardsTheRequestedExpiryAndProjects()
    {
        var apiKeys = ApiKeyServiceMock();
        var handler = BuildHandler(apiKeys, roleLevel: RoleService.RoleLevels.Editor);
        var expiresAt = Now.UtcDateTime.AddDays(10);

        await handler.Handle(new CreateApiKeyCommand("ci", ["read"], [4, 5], expiresAt), CancellationToken.None);

        apiKeys.Verify(
            x => x.GenerateApiKeyAsync(
                3,
                "ci",
                It.Is<string[]>(y => y.SequenceEqual(new[] { "Read" })),
                It.Is<int[]?>(y => y != null && y.SequenceEqual(new[] { 4, 5 })),
                expiresAt,
                0,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Handler_StampsTheKeyWithTheGenerationReadWithTheOwnersRecord()
    {
        var apiKeys = ApiKeyServiceMock();
        var owner = User(RoleService.RoleLevels.Editor);
        owner.ApiKeyGeneration = 4;
        var handler = new CreateApiKeyHandler(apiKeys.Object, Accessor(SessionPrincipal()), Users(owner).Object, Authorization(canWrite: true));

        await handler.Handle(new CreateApiKeyCommand("ci", ["Execute"], null, null), CancellationToken.None);

        apiKeys.Verify(x => x.GenerateApiKeyAsync(3, "ci", It.IsAny<string[]>(), null, null, 4, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task GeneratedKey_CarriesTheOwnerGenerationItWasIssuedWith()
    {
        var (service, added) = BuildService();

        var (credential, _) = await service.GenerateApiKeyAsync(7, "ci", ["Read"], ownerGeneration: 3);

        credential.OwnerGeneration.Should().Be(3);
        added.Should().ContainSingle().Which.OwnerGeneration.Should().Be(3);
    }

    [Test]
    public async Task DisabledUser_CannotIssueKeys()
    {
        var apiKeys = ApiKeyServiceMock();
        var handler = BuildHandler(apiKeys, enabled: false);

        var act = () => handler.Handle(new CreateApiKeyCommand("ci", ["Read"], null, null), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*disabled*");
        apiKeys.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ArchivedUser_IsNotFound_AndCannotIssueKeys()
    {
        var apiKeys = ApiKeyServiceMock();
        var handler = BuildHandler(apiKeys, found: false);

        var act = () => handler.Handle(new CreateApiKeyCommand("ci", ["Read"], null, null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
        apiKeys.VerifyNoOtherCalls();
    }

    [TestCase("api_key")]
    [TestCase("mcp_caller")]
    [TestCase("jwt")]
    [TestCase("anything")]
    public async Task CallerCarryingAnAuthMethod_CannotIssueKeys(string authMethod)
    {
        var apiKeys = ApiKeyServiceMock();
        var handler = BuildHandler(apiKeys, roleLevel: RoleService.RoleLevels.Editor, authMethod: authMethod);

        var act = () => handler.Handle(new CreateApiKeyCommand("ci", ["Read"], null, null), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*signed-in session*");
        apiKeys.VerifyNoOtherCalls();
    }

    [Test]
    public async Task UnauthenticatedCaller_CannotIssueKeys()
    {
        var apiKeys = ApiKeyServiceMock();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-ada")]));
        var handler = new CreateApiKeyHandler(apiKeys.Object, Accessor(anonymous), Users(User(RoleService.RoleLevels.Editor)).Object, Authorization(canWrite: true));

        var act = () => handler.Handle(new CreateApiKeyCommand("ci", ["Read"], null, null), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*signed-in session*");
        apiKeys.VerifyNoOtherCalls();
    }

    [Test]
    public async Task RetiredAdminScope_IsRefusedBeforeAnyPermissionCheck()
    {
        var apiKeys = ApiKeyServiceMock();
        var handler = BuildHandler(apiKeys, roleLevel: RoleService.RoleLevels.Editor);

        var act = () => handler.Handle(new CreateApiKeyCommand("ci", ["Execute", "Admin"], null, null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Admin scope*");
        apiKeys.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ViewersReachIssueAndOwnRevoke_ThroughThePermissionFilter()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddSingleton(Mock.Of<IMediator>());
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.MapGroup("/beacon/api").MapApiKeysEndpoints();

        var viewerEndpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .Where(x => x.Metadata.GetMetadata<BeaconViewerAccessMetadata>() != null)
            .Select(x => x.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
            .ToList();

        viewerEndpoints.Should().BeEquivalentTo("CreateApiKey", "RevokeApiKey");
    }

    // ── Options ──────────────────────────────────────────────────────────────

    [TestCase(1, true)]
    [TestCase(365, true)]
    [TestCase(3650, true)]
    [TestCase(0, false)]
    [TestCase(-5, false)]
    [TestCase(3651, false)]
    public void MaxLifetimeDays_IsValidated(int days, bool valid)
    {
        var result = new ApiKeyOptionsValidator().Validate(null, new ApiKeyOptions { MaxLifetimeDays = days });

        result.Succeeded.Should().Be(valid);
    }

    [Test]
    public void ApiKeysSection_BindsFromConfiguration()
    {
        using var provider = BuildBeaconProvider(new Dictionary<string, string?>
        {
            ["Beacon:ApiKeys:MaxLifetimeDays"] = "30",
            ["Beacon:ApiKeys:EnforceMaxLifetimeOnExistingKeys"] = "true"
        });

        var options = provider.GetRequiredService<IOptions<ApiKeyOptions>>().Value;

        options.MaxLifetimeDays.Should().Be(30);
        options.EnforceMaxLifetimeOnExistingKeys.Should().BeTrue();
    }

    [Test]
    public void ApiKeysSection_Defaults_WhenAbsent()
    {
        using var provider = BuildBeaconProvider([]);

        var options = provider.GetRequiredService<IOptions<ApiKeyOptions>>().Value;

        options.MaxLifetimeDays.Should().Be(365);
        options.EnforceMaxLifetimeOnExistingKeys.Should().BeFalse();
    }

    [TestCase("0")]
    [TestCase("3651")]
    public void MaxLifetimeDaysOutOfRange_FailsTheHostAtStartup(string days)
    {
        using var provider = BuildBeaconProvider(new Dictionary<string, string?> { ["Beacon:ApiKeys:MaxLifetimeDays"] = days });

        var act = () => provider.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<OptionsValidationException>().WithMessage("*Beacon:ApiKeys:MaxLifetimeDays*");
    }

    private static ServiceProvider BuildBeaconProvider(Dictionary<string, string?> settings)
    {
        // A throwaway encryption key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        settings["Beacon:EncryptionKey"] = Convert.ToBase64String(new byte[32]);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>())
            .UsePostgreSql("Host=localhost;Database=unused;Username=unused;Password=unused");

        return services.BuildServiceProvider();
    }

    private static (ApiKeyService Service, List<ApiKeyCredential> Added) BuildService(int maxLifetimeDays = 365)
    {
        var added = new List<ApiKeyCredential>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CapturingContext(added));
        var service = new ApiKeyService(
            factory.Object,
            Options.Create(new ApiKeyOptions { MaxLifetimeDays = maxLifetimeDays }),
            new FakeTimeProvider(Now),
            NullLogger<ApiKeyService>.Instance);

        return (service, added);
    }

    private static Mock<IApiKeyService> ApiKeyServiceMock()
    {
        var apiKeys = new Mock<IApiKeyService>();
        apiKeys
            .Setup(x => x.GenerateApiKeyAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<int[]?>(), It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ApiKeyCredential { Name = "ci", KeyHash = "h", KeyPrefix = "p" }, "sk-sem_issued"));
        return apiKeys;
    }

    private static CreateApiKeyHandler BuildHandler(
        Mock<IApiKeyService> apiKeys,
        int? roleLevel = RoleService.RoleLevels.Viewer,
        bool providerCanWrite = true,
        bool isSuperAdmin = false,
        bool enabled = true,
        bool found = true,
        string? authMethod = null)
    {
        var principal = authMethod == null
            ? SessionPrincipal()
            : SessionPrincipal(new Claim(McpCallerClaimTypes.AuthMethod, authMethod));
        var user = found ? User(roleLevel, isSuperAdmin, enabled) : null;

        return new CreateApiKeyHandler(apiKeys.Object, Accessor(principal), Users(user).Object, Authorization(providerCanWrite));
    }

    // A signed-in browser session: NameIdentifier carries Users.ExternalId.
    private static ClaimsPrincipal SessionPrincipal(params Claim[] extra) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-ada"), .. extra], "Beacon.Auth"));

    private static IHttpContextAccessor Accessor(ClaimsPrincipal principal) =>
        new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };

    private static BeaconUserData User(int? roleLevel, bool isSuperAdmin = false, bool enabled = true) =>
        new()
        {
            Id = 3,
            ExternalId = "ext-ada",
            UserName = "ada",
            IsEnabled = enabled,
            IsSuperAdmin = isSuperAdmin,
            Roles = roleLevel == null ? [] : [new BeaconRoleData { Name = "role", Level = roleLevel.Value }]
        };

    private static Mock<IUserManagementService> Users(BeaconUserData? user)
    {
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.GetUserByExternalIdAsync("ext-ada", It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        return users;
    }

    private static IBeaconAuthorizationProvider Authorization(bool canWrite)
    {
        var authorization = new Mock<IBeaconAuthorizationProvider>();
        authorization
            .Setup(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(canWrite);
        return authorization.Object;
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }

    /// <summary>A BeaconContext whose <c>ApiKeyCredentials</c> set records what is added; nothing is persisted.</summary>
    private sealed class CapturingContext(List<ApiKeyCredential> added) : BeaconContext(ContextOptions, "beacon")
    {
        private static readonly DbContextOptions<CapturingContext> ContextOptions =
            new DbContextOptionsBuilder<CapturingContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public override DbSet<TEntity> Set<TEntity>()
        {
            if (typeof(TEntity) == typeof(ApiKeyCredential))
            {
                var set = new Mock<DbSet<ApiKeyCredential>>();
                set
                    .Setup(x => x.Add(It.IsAny<ApiKeyCredential>()))
                    .Callback((ApiKeyCredential x) => added.Add(x));
                return (DbSet<TEntity>)(object)set.Object;
            }

            return base.Set<TEntity>();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(added.Count);
    }
}
