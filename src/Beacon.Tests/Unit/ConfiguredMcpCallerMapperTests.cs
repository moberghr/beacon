using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
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
    private const string OtherTenantId = "99999999-9999-9999-9999-999999999999";
    private const string Audience = "api://beacon";
    private const string Issuer = "https://login.microsoftonline.com/" + TenantId + "/v2.0";

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
        using var provider = BuildBeaconProvider(
            new Dictionary<string, string?>
            {
                ["Beacon:Mcp:Callers:AllowedTenants:0"] = TenantId,
                ["Beacon:Mcp:Callers:Systems:0:Name"] = "aiproxy",
                ["Beacon:Mcp:Callers:Systems:0:ClientId"] = AiProxyClientId,
                ["Beacon:Mcp:Callers:Systems:0:ObjectId"] = ServiceUserOid
            },
            BearerOptions());

        var act = () => provider.GetRequiredService<IOptions<McpCallerOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*must set exactly one of ClientId or ObjectId*");
    }

    [Test]
    public void Startup_RegistersTheConfiguredMapper_WithBoundOptions()
    {
        using var provider = BuildBeaconProvider(
            new Dictionary<string, string?>
            {
                ["Beacon:Mcp:Callers:AllowedTenants:0"] = TenantId,
                ["Beacon:Mcp:Callers:Users:Enabled"] = "true",
                ["Beacon:Mcp:Callers:Users:RequiredGroups:0"] = AnalystsGroup,
                ["Beacon:Mcp:Callers:Users:Scope"] = "Read",
                ["Beacon:Mcp:Callers:Users:GroupProjects:" + AnalystsGroup + ":0"] = "4",
                ["Beacon:Mcp:Callers:Systems:0:Name"] = "aiproxy",
                ["Beacon:Mcp:Callers:Systems:0:ClientId"] = AiProxyClientId
            },
            BearerOptions());
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

    [Test]
    public async Task Tenant_NotAllowed_OrMissing_IsRejected()
    {
        var mapper = CreateMapper(AiProxySystem());

        var otherTenant = await mapper.MapAsync(AppToken(AiProxyClientId, AiProxyServicePrincipalOid, tenantId: OtherTenantId), CancellationToken.None);
        var noTenant = await mapper.MapAsync(AppToken(AiProxyClientId, AiProxyServicePrincipalOid, tenantId: null), CancellationToken.None);
        var allowed = await mapper.MapAsync(AppToken(AiProxyClientId, AiProxyServicePrincipalOid), CancellationToken.None);

        otherTenant.Should().BeNull("Entra's JWKS signs tokens for every tenant; only AllowedTenants may call");
        noTenant.Should().BeNull("a token without tid cannot be tied to an allowed tenant");
        allowed!.Name.Should().Be("aiproxy");
    }

    [Test]
    public async Task Audience_NotConfigured_IsRejected_EvenWhenTheBearerLayerLetItThrough()
    {
        var mapper = CreateMapper(AiProxySystem());
        var mapperWithoutBearerOptions = CreateMapper(AiProxySystem(), withJwtOptions: false);

        var foreignAudience = await mapper.MapAsync(AppToken(AiProxyClientId, AiProxyServicePrincipalOid, audience: "https://graph.microsoft.com"), CancellationToken.None);
        var noAudiencesKnown = await mapperWithoutBearerOptions.MapAsync(AppToken(AiProxyClientId, AiProxyServicePrincipalOid), CancellationToken.None);

        foreignAudience.Should().BeNull("a token minted for another resource must never map to a caller");
        noAudiencesKnown.Should().BeNull("with no configured audience nothing is accepted (fail closed)");
    }

    [Test]
    public async Task IdToken_WithTheSystemsAzp_IsNotTheSystem()
    {
        var mapper = CreateMapper(AiProxySystem());
        var idToken = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tid", TenantId),
                new Claim("aud", Audience),
                new Claim("oid", UserOid),
                new Claim("sub", "pairwise-subject"),
                new Claim("azp", AiProxyClientId),
                new Claim("nonce", "abc"),
                new Claim("roles", "Mcp.System")
            ],
            "Bearer"));

        var caller = await mapper.MapAsync(idToken, CancellationToken.None);

        caller.Should().BeNull("an ID token is not an access token, whatever its azp");
    }

    [Test]
    public async Task ScpLessToken_WithoutAPositiveAppOnlySignal_IsRejected()
    {
        var mapper = CreateMapper(AiProxySystem());

        var neitherScpNorRoles = await mapper.MapAsync(AppToken(AiProxyClientId, AiProxyServicePrincipalOid, withIdType: false, withRoles: false), CancellationToken.None);
        var rolesButUserSubject = await mapper.MapAsync(
            new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tid", TenantId),
                    new Claim("aud", Audience),
                    new Claim("oid", UserOid),
                    new Claim("sub", "pairwise-subject"),
                    new Claim("azp", AiProxyClientId),
                    new Claim("roles", "Mcp.System")
                ],
                "Bearer")),
            CancellationToken.None);

        neitherScpNorRoles.Should().BeNull("\"no scp\" alone is not an app-only signal");
        rolesButUserSubject.Should().BeNull("oid != sub is a user's token, not a service principal's");
    }

    [Test]
    public async Task AppOnlyToken_WithoutIdtyp_IsRecognisedByRolesAndOidEqualsSub()
    {
        var mapper = CreateMapper(AiProxySystem());

        var caller = await mapper.MapAsync(AppToken(AiProxyClientId, AiProxyServicePrincipalOid, withIdType: false), CancellationToken.None);

        caller!.Name.Should().Be("aiproxy");
    }

    [Test]
    public void ClassifyToken_IdtypAppWithScp_IsContradictory()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("idtyp", "app"), new Claim("scp", "x")], "Bearer"));

        ConfiguredMcpCallerMapper.ClassifyToken(principal).Should().Be(McpTokenKind.Unrecognized);
    }

    [Test]
    public async Task UserMode_NoRequirements_WithoutTheExplicitOptIn_RejectsUsers()
    {
        var mapper = CreateMapper(UsersEnabled(x =>
        {
            x.ProjectIds = [1];
            x.AllowAnyTenantUser = false;
        }));

        var caller = await mapper.MapAsync(UserToken(), CancellationToken.None);

        caller.Should().BeNull("an empty role/group requirement admits nobody unless AllowAnyTenantUser is set");
    }

    [Test]
    public async Task UserMode_ConcurrentFirstProvisioning_UniqueViolation_RereadsTheUser()
    {
        var userService = new Mock<IUserManagementService>();
        userService
            .SetupSequence(x => x.GetOrCreateExternalUserAsync(
                UserOid,
                $"entra:{TenantId}",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("insert failed", new PostgresException("duplicate key", "ERROR", "ERROR", "23505")))
            .ReturnsAsync(new BeaconUserData { Id = 21, ExternalId = UserOid, UserName = "ana" });
        var mapper = CreateMapper(UsersEnabled(x => x.AutoProvision = true), userService.Object);

        var caller = await mapper.MapAsync(UserToken(), CancellationToken.None);

        caller!.BeaconUserId.Should().Be(21, "the losing request re-reads the user the winning request created");
    }

    [Test]
    public void UniqueViolation_RecognisesPostgresAndIgnoresOtherErrors()
    {
        DbUniqueViolation.IsUniqueViolation(new DbUpdateException("x", new PostgresException("dup", "ERROR", "ERROR", "23505"))).Should().BeTrue();
        DbUniqueViolation.IsUniqueViolation(new DbUpdateException("x", new PostgresException("fk", "ERROR", "ERROR", "23503"))).Should().BeFalse();
        DbUniqueViolation.IsUniqueViolation(new InvalidOperationException("x")).Should().BeFalse();
    }

    [Test]
    public void Validator_ActiveCallers_RequireTenantsIssuerAndAudience()
    {
        var options = new McpCallerOptions { Systems = [new McpSystemCallerOptions { Name = "a", ClientId = "c1" }] };

        var noBearer = new McpCallerOptionsValidator().Validate(null, options);
        var noIssuer = new McpCallerOptionsValidator(BearerOptions(issuer: null)).Validate(null, options);
        var issuerOff = new McpCallerOptionsValidator(BearerOptions(validateIssuer: false)).Validate(null, options);
        var noAudience = new McpCallerOptionsValidator(BearerOptions(audience: null)).Validate(null, options);

        noBearer.FailureMessage.Should().Contain("AllowedTenants").And.Contain("bearer JWT authentication is not enabled");
        noIssuer.FailureMessage.Should().Contain("issuer validation");
        issuerOff.FailureMessage.Should().Contain("issuer validation");
        noAudience.FailureMessage.Should().Contain("audience validation");
    }

    [Test]
    public void Validator_InactiveCallers_NeedNoBearerSettings()
    {
        var result = new McpCallerOptionsValidator().Validate(null, new McpCallerOptions());

        result.Succeeded.Should().BeTrue("an absent callers section accepts nobody and needs nothing");
    }

    [Test]
    public void Validator_UsersWithoutRequirements_NeedTheExplicitOptIn()
    {
        var withoutOptIn = new McpCallerOptions { AllowedTenants = [TenantId], Users = new McpUserCallerOptions { Enabled = true } };
        var withOptIn = new McpCallerOptions { AllowedTenants = [TenantId], Users = new McpUserCallerOptions { Enabled = true, AllowAnyTenantUser = true } };

        var failed = new McpCallerOptionsValidator(BearerOptions()).Validate(null, withoutOptIn);
        var succeeded = new McpCallerOptionsValidator(BearerOptions()).Validate(null, withOptIn);

        failed.FailureMessage.Should().Contain("AllowAnyTenantUser");
        succeeded.Succeeded.Should().BeTrue();
    }

    [Test]
    public void Startup_ActiveCallersWithoutIssuerValidation_FailsTheHost()
    {
        using var provider = BuildBeaconProvider(
            new Dictionary<string, string?>
            {
                ["Beacon:Mcp:Callers:AllowedTenants:0"] = TenantId,
                ["Beacon:Mcp:Callers:Systems:0:Name"] = "aiproxy",
                ["Beacon:Mcp:Callers:Systems:0:ClientId"] = AiProxyClientId
            },
            BearerOptions(issuer: null));

        var act = () => provider.GetRequiredService<IOptions<McpCallerOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*issuer validation*");
    }

    private static ConfiguredMcpCallerMapper CreateMapper(
        McpCallerOptions options,
        IUserManagementService? userService = null,
        JwtAuthenticationOptions? jwtOptions = null,
        bool withJwtOptions = true)
    {
        if (options.AllowedTenants.Count == 0)
        {
            options.AllowedTenants.Add(TenantId);
        }

        return new ConfiguredMcpCallerMapper(
            Options.Create(options),
            new MemoryCache(new MemoryCacheOptions()),
            Hasher,
            NullLogger<ConfiguredMcpCallerMapper>.Instance,
            userService,
            withJwtOptions ? jwtOptions ?? BearerOptions() : null);
    }

    private static JwtAuthenticationOptions BearerOptions(string? issuer = Issuer, string? audience = Audience, bool validateIssuer = true, bool validateAudience = true)
    {
        return new JwtAuthenticationOptions
        {
            EnableBearerAuthentication = true,
            Validation = new JwtValidationOptions
            {
                JwksEndpoint = "https://login.microsoftonline.com/common/discovery/v2.0/keys",
                ValidIssuer = issuer,
                ValidAudience = audience,
                ValidateIssuer = validateIssuer,
                ValidateAudience = validateAudience
            }
        };
    }

    private static McpCallerOptions UsersEnabled(Action<McpUserCallerOptions> configure)
    {
        var users = new McpUserCallerOptions { Enabled = true, AutoProvision = false, AllowAnyTenantUser = true };
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
            new("aud", Audience),
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

    private static ClaimsPrincipal AppToken(string appId, string servicePrincipalOid, bool withIdType = true, bool withRoles = true, string? tenantId = TenantId, string audience = Audience)
    {
        var claims = new List<Claim>
        {
            new("aud", audience),
            new("oid", servicePrincipalOid),
            new("sub", servicePrincipalOid),
            new("azp", appId)
        };
        if (tenantId != null)
        {
            claims.Add(new Claim("tid", tenantId));
        }

        if (withIdType)
        {
            claims.Add(new Claim("idtyp", "app"));
        }

        if (withRoles)
        {
            claims.Add(new Claim("roles", "Mcp.System"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static McpCallerOptions AiProxySystem() =>
        new()
        {
            Systems = [new McpSystemCallerOptions { Name = "aiproxy", ClientId = AiProxyClientId, ProjectIds = [7] }]
        };

    private static ValidateOptionsResult Validate(params McpSystemCallerOptions[] systems)
    {
        return new McpCallerOptionsValidator(BearerOptions()).Validate(null, new McpCallerOptions { AllowedTenants = [TenantId], Systems = [.. systems] });
    }

    private static ServiceProvider BuildBeaconProvider(Dictionary<string, string?> settings, JwtAuthenticationOptions? jwtOptions = null)
    {
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        settings["Beacon:EncryptionKey"] = Convert.ToBase64String(new byte[32]);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        if (jwtOptions != null)
        {
            services.AddSingleton(jwtOptions);
        }

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
