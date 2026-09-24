using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.PostgreSql;
using Beacon.Core.Services;
using Beacon.Core.Worker;

namespace Beacon.Tests.Unit;

/// <summary>
/// P1 gateway — <see cref="ConfiguredMcpCallerMapper"/> decides which Entra JWT callers reach MCP, with which projects
/// and scope. Every "no" here must be a null (fail closed), and nothing in the token may widen what config grants.
/// </summary>
[TestFixture]
public class ConfiguredMcpCallerMapperTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string UserOid = "22222222-2222-2222-2222-222222222222";
    private const string ServiceUserOid = "33333333-3333-3333-3333-333333333333";
    private const string AiProxyClientId = "44444444-4444-4444-4444-444444444444";
    private const string AiProxyServicePrincipalOid = "55555555-5555-5555-5555-555555555555";
    private const string AnalystsGroup = "66666666-6666-6666-6666-666666666666";

    // Generated per run: a test-only key, never a real secret (§1.2).
    private static readonly string EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly McpCallerSubjectHasher Hasher = new(EncryptionKey);

    [Test]
    public async Task UserMode_Disabled_RejectsDelegatedToken()
    {
        var mapper = CreateMapper(new McpCallerOptions());

        var caller = await mapper.MapAsync(UserToken(), CancellationToken.None);

        caller.Should().BeNull("user mode is off by default");
    }

    [Test]
    public async Task UserMode_NoRequirements_AcceptsUserWithBaseProjectsAndConfiguredScope()
    {
        var mapper = CreateMapper(UsersEnabled(x =>
        {
            x.ProjectIds = [5, 3];
            x.Scope = McpCallerScope.Read;
        }));

        var caller = await mapper.MapAsync(UserToken(), CancellationToken.None);

        caller.Should().NotBeNull();
        caller!.Kind.Should().Be(McpCallerKind.User);
        caller.AllowedProjectIds.Should().Equal(3, 5);
        caller.Scope.Should().Be(McpCallerScope.Read);
        caller.BeaconUserId.Should().BeNull("AutoProvision is off in this test");
        caller.HostClaims.Should().BeEmpty();
    }

    [Test]
    public async Task UserMode_RequiredRole_AdmitsHolderAndRejectsOthers()
    {
        var mapper = CreateMapper(UsersEnabled(x => x.RequiredRoles = ["Beacon.Reader"]));

        var holder = await mapper.MapAsync(UserToken(roles: ["Beacon.Reader"]), CancellationToken.None);
        var other = await mapper.MapAsync(UserToken(roles: ["Something.Else"]), CancellationToken.None);

        holder.Should().NotBeNull();
        other.Should().BeNull();
    }

    [Test]
    public async Task UserMode_RolesAndGroupsAreAnyOf_GroupAloneAdmits()
    {
        var mapper = CreateMapper(UsersEnabled(x =>
        {
            x.RequiredRoles = ["Beacon.Reader"];
            x.RequiredGroups = [AnalystsGroup];
        }));

        var byGroup = await mapper.MapAsync(UserToken(groups: [AnalystsGroup]), CancellationToken.None);
        var neither = await mapper.MapAsync(UserToken(groups: ["77777777-0000-0000-0000-000000000000"]), CancellationToken.None);

        byGroup.Should().NotBeNull();
        neither.Should().BeNull();
    }

    [Test]
    public async Task UserMode_GroupProjects_AreUnionedWithBaseProjects()
    {
        var mapper = CreateMapper(UsersEnabled(x =>
        {
            x.ProjectIds = [1];
            x.GroupProjects = new Dictionary<string, List<int>>
            {
                [AnalystsGroup] = [2, 3],
                ["Beacon.Finance"] = [3, 4],
                ["not-a-member"] = [99]
            };
        }));

        var caller = await mapper.MapAsync(
            UserToken(roles: ["beacon.finance"], groups: [AnalystsGroup]),
            CancellationToken.None);

        caller!.AllowedProjectIds.Should().Equal(1, 2, 3, 4);
    }

    [Test]
    public async Task UserMode_TokenSuppliedProjectsAndScope_AreIgnored()
    {
        var mapper = CreateMapper(UsersEnabled(x =>
        {
            x.ProjectIds = [1];
            x.Scope = McpCallerScope.Read;
        }));
        var token = UserToken(extra:
        [
            new Claim("allowed_projects", "[99]"),
            new Claim("scope", "Admin")
        ]);

        var caller = await mapper.MapAsync(token, CancellationToken.None);

        caller!.AllowedProjectIds.Should().Equal(1);
        caller.Scope.Should().Be(McpCallerScope.Read);
    }

    [Test]
    public async Task UserMode_AutoProvision_KeysOnOidAndTenant_AndCachesTheUserId()
    {
        var userService = new Mock<IUserManagementService>();
        userService
            .Setup(x => x.GetOrCreateExternalUserAsync(
                UserOid,
                $"entra:{TenantId}",
                "ana@contoso.example",
                "ana@contoso.example",
                "Ana Analyst",
                "Viewer",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeaconUserData { Id = 17, ExternalId = UserOid, UserName = "ana@contoso.example" });
        var mapper = CreateMapper(UsersEnabled(x => x.AutoProvision = true), userService.Object);

        var first = await mapper.MapAsync(UserToken(), CancellationToken.None);
        var second = await mapper.MapAsync(UserToken(), CancellationToken.None);

        first!.BeaconUserId.Should().Be(17);
        second!.BeaconUserId.Should().Be(17);
        userService.Verify(
            x => x.GetOrCreateExternalUserAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the provisioned id is cached, so every MCP message does not hit the users table");
    }

    [Test]
    public async Task UserMode_ProvisioningRefused_RejectsTheCaller()
    {
        var userService = new Mock<IUserManagementService>();
        userService
            .Setup(x => x.GetOrCreateExternalUserAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BeaconException("This account has been disabled."));
        var mapper = CreateMapper(UsersEnabled(x => x.AutoProvision = true), userService.Object);

        var caller = await mapper.MapAsync(UserToken(), CancellationToken.None);

        caller.Should().BeNull("a disabled or archived Beacon user must not reach MCP");
    }

    [Test]
    public async Task System_ByClientId_MatchesAppOnlyToken()
    {
        var mapper = CreateMapper(new McpCallerOptions
        {
            Systems =
            [
                new McpSystemCallerOptions
                {
                    Name = "aiproxy",
                    ClientId = AiProxyClientId,
                    ProjectIds = [7, 7, 8],
                    Scope = McpCallerScope.Read,
                    HostClaims = [new McpHostClaimOptions { Type = "permission", Value = "ViewLoans" }]
                }
            ]
        });

        var caller = await mapper.MapAsync(AppToken(AiProxyClientId, AiProxyServicePrincipalOid), CancellationToken.None);

        caller.Should().NotBeNull();
        caller!.Kind.Should().Be(McpCallerKind.System);
        caller.Name.Should().Be("aiproxy");
        caller.AllowedProjectIds.Should().Equal(7, 8);
        caller.Scope.Should().Be(McpCallerScope.Read);
        caller.BeaconUserId.Should().BeNull();
        caller.HostClaims.Select(x => (x.Type, x.Value)).Should().Equal(("permission", "ViewLoans"));
    }

    [Test]
    public async Task System_ByClientId_DoesNotCaptureDelegatedUserTokensFromTheSameApp()
    {
        // A delegated user token issued to AIProxy carries AIProxy's azp. Matching the system on it would silently
        // run every user as the system identity.
        var mapper = CreateMapper(new McpCallerOptions
        {
            Systems = [new McpSystemCallerOptions { Name = "aiproxy", ClientId = AiProxyClientId, ProjectIds = [7] }]
        });

        var caller = await mapper.MapAsync(UserToken(azp: AiProxyClientId), CancellationToken.None);

        caller.Should().BeNull("user mode is disabled, and a delegated token is never a ClientId system");
    }

    [Test]
    public async Task System_ByObjectId_MatchesServiceUserDelegatedToken()
    {
        var mapper = CreateMapper(new McpCallerOptions
        {
            Users = new McpUserCallerOptions { Enabled = true, ProjectIds = [1] },
            Systems = [new McpSystemCallerOptions { Name = "routine-runner", ObjectId = ServiceUserOid, ProjectIds = [9] }]
        });

        var caller = await mapper.MapAsync(UserToken(oid: ServiceUserOid), CancellationToken.None);

        caller!.Kind.Should().Be(McpCallerKind.System, "a configured service user wins over user mode");
        caller.Name.Should().Be("routine-runner");
        caller.AllowedProjectIds.Should().Equal(9);
    }

    [Test]
    public async Task AppOnlyToken_FromUnknownClient_IsRejectedEvenWithUserModeOn()
    {
        var mapper = CreateMapper(UsersEnabled(x => x.ProjectIds = [1]));

        var caller = await mapper.MapAsync(AppToken("88888888-0000-0000-0000-000000000000", AiProxyServicePrincipalOid), CancellationToken.None);

        caller.Should().BeNull("an application is never admitted as a user");
    }

    [Test]
    public async Task TokenWithoutAnySubject_IsRejected()
    {
        var mapper = CreateMapper(UsersEnabled(x => x.ProjectIds = [1]));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("scp", "Mcp.Access")], "Bearer"));

        var caller = await mapper.MapAsync(principal, CancellationToken.None);

        caller.Should().BeNull();
    }

    [Test]
    public async Task SubjectHash_IsTheKeyedHashOfTenantAndOid_AndRevealsNeither()
    {
        var mapper = CreateMapper(UsersEnabled(x => x.ProjectIds = [1]));

        var caller = await mapper.MapAsync(UserToken(), CancellationToken.None);

        caller!.SubjectHash.Should().MatchRegex("^[0-9a-f]{64}$");
        caller.SubjectHash.Should().Be(Hasher.Hash(TenantId, UserOid));
        caller.SubjectHash.Should().NotContain(UserOid).And.NotContain(TenantId);
        Hasher.Hash("other-tenant", UserOid).Should().NotBe(caller.SubjectHash);
    }

    [Test]
    public void Hasher_SameInputAndKey_GivesTheSameHash()
    {
        var first = new McpCallerSubjectHasher(EncryptionKey).Hash(TenantId, UserOid);
        var second = new McpCallerSubjectHasher(EncryptionKey).Hash(TenantId, UserOid);

        first.Should().MatchRegex("^[0-9a-f]{64}$");
        second.Should().Be(first, "audit rows for one identity must group together");
    }

    [Test]
    public void Hasher_DifferentKey_GivesADifferentHash()
    {
        var otherKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var hash = new McpCallerSubjectHasher(otherKey).Hash(TenantId, UserOid);

        hash.Should().NotBe(Hasher.Hash(TenantId, UserOid));
    }

    [Test]
    public void Hasher_IsKeyed_NotAPlainHash_AndDoesNotUseTheEncryptionKeyDirectly()
    {
        var input = Encoding.UTF8.GetBytes($"{TenantId}:{UserOid}");
        var plainSha256 = Convert.ToHexStringLower(SHA256.HashData(input));
        var hmacWithRawKey = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(EncryptionKey), input));

        var hash = Hasher.Hash(TenantId, UserOid);

        hash.Should().NotBe(plainSha256, "an unkeyed hash of an oid can be reversed by hashing the tenant's oid list");
        hash.Should().NotBe(hmacWithRawKey, "the HMAC key is derived for this purpose, not the encryption key itself");
    }

    [Test]
    public void Hasher_WithoutAKey_Throws()
    {
        var act = () => new McpCallerSubjectHasher(" ");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Startup_RegistersTheHasher_KeyedByTheConfiguredEncryptionKey()
    {
        using var provider = BuildBeaconProvider(new Dictionary<string, string?>());

        var hasher = provider.GetRequiredService<McpCallerSubjectHasher>();

        hasher.Hash(TenantId, UserOid).Should().Be(
            new McpCallerSubjectHasher(Convert.ToBase64String(new byte[32])).Hash(TenantId, UserOid));
        hasher.Hash(TenantId, UserOid).Should().NotBe(Hasher.Hash(TenantId, UserOid));
    }

    [Test]
    public void Validator_SystemWithBothClientIdAndObjectId_Fails()
    {
        var result = Validate(new McpSystemCallerOptions { Name = "x", ClientId = AiProxyClientId, ObjectId = ServiceUserOid });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Beacon:Mcp:Callers:Systems:0 must set exactly one of ClientId or ObjectId");
    }

    [Test]
    public void Validator_SystemWithNeitherClientIdNorObjectId_Fails()
    {
        var result = Validate(new McpSystemCallerOptions { Name = "x" });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("exactly one of ClientId or ObjectId");
    }

    [Test]
    public void Validator_DuplicateNamesAndMissingName_Fail()
    {
        var result = Validate(
            new McpSystemCallerOptions { Name = "a", ClientId = "c1" },
            new McpSystemCallerOptions { Name = "A", ClientId = "c2" },
            new McpSystemCallerOptions { ObjectId = "o1" });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Name 'A' is used by more than one system caller");
        result.FailureMessage.Should().Contain("Systems:2:Name is required");
    }

    [Test]
    public void Validator_WellFormedSystems_Succeed()
    {
        var result = Validate(
            new McpSystemCallerOptions { Name = "a", ClientId = "c1", ProjectIds = [1] },
            new McpSystemCallerOptions { Name = "b", ObjectId = "o1", HostClaims = [new McpHostClaimOptions { Type = "t", Value = "v" }] });

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public void Startup_BindsCallersSection_AndRejectsAnInvalidSystem()
    {
        using var provider = BuildBeaconProvider(new Dictionary<string, string?>
        {
            ["Beacon:Mcp:Callers:Systems:0:Name"] = "aiproxy",
            ["Beacon:Mcp:Callers:Systems:0:ClientId"] = AiProxyClientId,
            ["Beacon:Mcp:Callers:Systems:0:ObjectId"] = ServiceUserOid
        });

        var act = () => provider.GetRequiredService<IOptions<McpCallerOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*must set exactly one of ClientId or ObjectId*");
    }

    [Test]
    public void Startup_RegistersTheConfiguredMapper_WithBoundOptions()
    {
        using var provider = BuildBeaconProvider(new Dictionary<string, string?>
        {
            ["Beacon:Mcp:Callers:Users:Enabled"] = "true",
            ["Beacon:Mcp:Callers:Users:Scope"] = "Read",
            ["Beacon:Mcp:Callers:Users:GroupProjects:" + AnalystsGroup + ":0"] = "4",
            ["Beacon:Mcp:Callers:Systems:0:Name"] = "aiproxy",
            ["Beacon:Mcp:Callers:Systems:0:ClientId"] = AiProxyClientId
        });
        using var scope = provider.CreateScope();

        var mapper = scope.ServiceProvider.GetRequiredService<IMcpCallerMapper>();
        var options = provider.GetRequiredService<IOptions<McpCallerOptions>>().Value;

        mapper.Should().BeOfType<ConfiguredMcpCallerMapper>();
        options.Users.Enabled.Should().BeTrue();
        options.Users.Scope.Should().Be(McpCallerScope.Read);
        options.Users.AutoProvision.Should().BeTrue("AutoProvision defaults to on");
        options.Users.GroupProjects[AnalystsGroup].Should().Equal(4);
        options.Systems.Should().ContainSingle(x => x.Name == "aiproxy" && x.ClientId == AiProxyClientId);
    }

    private static ConfiguredMcpCallerMapper CreateMapper(McpCallerOptions options, IUserManagementService? userService = null)
    {
        return new ConfiguredMcpCallerMapper(
            Options.Create(options),
            new MemoryCache(new MemoryCacheOptions()),
            Hasher,
            NullLogger<ConfiguredMcpCallerMapper>.Instance,
            userService);
    }

    private static McpCallerOptions UsersEnabled(Action<McpUserCallerOptions> configure)
    {
        var users = new McpUserCallerOptions { Enabled = true, AutoProvision = false };
        configure(users);

        return new McpCallerOptions { Users = users };
    }

    private static ClaimsPrincipal UserToken(
        string oid = UserOid,
        string azp = AiProxyClientId,
        string[]? roles = null,
        string[]? groups = null,
        Claim[]? extra = null)
    {
        var claims = new List<Claim>
        {
            new("tid", TenantId),
            new("oid", oid),
            new("sub", "pairwise-subject"),
            new("azp", azp),
            new("scp", "Mcp.Access"),
            new("preferred_username", "ana@contoso.example"),
            new("email", "ana@contoso.example"),
            new("name", "Ana Analyst")
        };
        claims.AddRange((roles ?? []).Select(x => new Claim("roles", x)));
        claims.AddRange((groups ?? []).Select(x => new Claim("groups", x)));
        claims.AddRange(extra ?? []);

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static ClaimsPrincipal AppToken(string appId, string servicePrincipalOid)
    {
        var claims = new List<Claim>
        {
            new("tid", TenantId),
            new("oid", servicePrincipalOid),
            new("sub", servicePrincipalOid),
            new("azp", appId),
            new("idtyp", "app"),
            new("roles", "Mcp.System")
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static ValidateOptionsResult Validate(params McpSystemCallerOptions[] systems)
    {
        return new McpCallerOptionsValidator().Validate(null, new McpCallerOptions { Systems = [.. systems] });
    }

    private static ServiceProvider BuildBeaconProvider(Dictionary<string, string?> settings)
    {
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        settings["Beacon:EncryptionKey"] = Convert.ToBase64String(new byte[32]);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>())
            .UsePostgreSql("Host=localhost;Database=unused;Username=unused;Password=unused");

        return services.BuildServiceProvider();
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
