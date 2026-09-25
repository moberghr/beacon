using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using NUnit.Framework;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Configuration;
using Beacon.Core.Mcp;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.MCP.Services;

namespace Beacon.Tests.Unit;

/// <summary>
/// P1 gateway, end to end through <see cref="JwtBearerAuthMiddleware"/>: a signed Entra-shaped token on
/// <c>/beacon/mcp</c> becomes the claims <see cref="ProjectContextFactory"/> and the Execute-scope policy already
/// understand, decided by the mapper and never by the token itself.
/// </summary>
[TestFixture]
public class JwtBearerMcpCallerMiddlewareTests
{
    private const string Issuer = "https://login.microsoftonline.com/tenant-1/v2.0";
    private const string Audience = "api://beacon";
    private const string TenantId = "tenant-1";
    private const string AiProxyClientId = "aiproxy-client";
    private const string UserOid = "user-oid-1";

    // Generated per run: a test-only HMAC key, never a real secret (§1.2).
    private static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    private static readonly McpCallerSubjectHasher Hasher = new(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    private static readonly McpCallerOptions Callers = new()
    {
        Users = new McpUserCallerOptions { Enabled = true, ProjectIds = [3], Scope = McpCallerScope.Execute },
        Systems =
        [
            new McpSystemCallerOptions { Name = "aiproxy", ClientId = AiProxyClientId, ProjectIds = [7], Scope = McpCallerScope.Read }
        ]
    };

    [Test]
    public async Task McpRoute_SystemToken_GetsMapperDecidedClaims_NotTokenSuppliedOnes()
    {
        var token = MintToken(
            AppOnlyClaims(AiProxyClientId),
            new Claim("allowed_projects", "[99]"),
            new Claim("scope", "Admin"),
            new Claim("caller_kind", "User"),
            new Claim("api_key_id", "5"));

        var (context, user) = await RunAsync("/beacon/mcp", token);

        user.FindAll("allowed_projects").Select(x => x.Value).Should().Equal("[7]");
        user.FindAll("scope").Select(x => x.Value).Should().Equal("Read");
        user.FindAll("caller_kind").Select(x => x.Value).Should().Equal("System");
        user.FindAll("auth_method").Select(x => x.Value).Should().Equal("mcp_caller");
        user.FindFirst("api_key_id").Should().BeNull();
        var expectedHash = Hasher.Hash(TenantId, "sp-oid");
        user.FindFirst("caller_hash")!.Value.Should().Be(expectedHash);
        user.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be(expectedHash, "no Beacon user → the hash, never the raw sub");
        context.Items[typeof(McpCaller)].Should().BeOfType<McpCaller>()
            .Which.HostClaims.Should().BeEmpty();

        var projectContext = CreateProjectContext(context);
        projectContext.AllowedProjectIds.Should().Equal(7);
        projectContext.UserId.Should().BeNull();
        projectContext.ApiKeyId.Should().BeNull("a token cannot pose as an API key");
    }

    [Test]
    public async Task McpRoute_ReadScopedSystem_IsDeniedByTheExecutePolicy()
    {
        var (_, user) = await RunAsync("/beacon/mcp", MintToken(AppOnlyClaims(AiProxyClientId)));

        (await AuthorizeExecuteAsync(user)).Should().BeFalse();
    }

    [Test]
    public async Task McpRoute_ProvisionedUser_CarriesBeaconUserId_AndPassesThePolicy()
    {
        var userService = new Mock<IUserManagementService>();
        userService
            .Setup(x => x.GetOrCreateExternalUserAsync(
                UserOid,
                $"entra:{TenantId}",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeaconUserData { Id = 17, ExternalId = UserOid, UserName = "ana" });
        var options = new McpCallerOptions
        {
            Users = new McpUserCallerOptions { Enabled = true, ProjectIds = [3], AutoProvision = true }
        };

        var (context, user) = await RunAsync("/beacon/mcp", MintToken(DelegatedClaims()), options, userService.Object);

        user.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("17");
        user.FindFirst("caller_kind")!.Value.Should().Be("User");
        (await AuthorizeExecuteAsync(user)).Should().BeTrue();

        var projectContext = CreateProjectContext(context);
        projectContext.UserId.Should().Be(17, "audit rows get the Beacon user");
        projectContext.AllowedProjectIds.Should().Equal(3);
    }

    [Test]
    public async Task McpRoute_UnknownCaller_HasNoScopeAndNoProjects()
    {
        var (context, user) = await RunAsync("/beacon/mcp", MintToken(AppOnlyClaims("someone-else"), new Claim("allowed_projects", "[1]")));

        user.Identity!.IsAuthenticated.Should().BeTrue();
        user.FindFirst("scope").Should().BeNull();
        user.FindFirst("allowed_projects").Should().BeNull();
        context.Items.ContainsKey(typeof(McpCaller)).Should().BeFalse();
        (await AuthorizeExecuteAsync(user)).Should().BeFalse("an unknown JWT caller gets 403 on /beacon/mcp");
        CreateProjectContext(context).AllowedProjectIds.Should().BeEmpty();
    }

    [Test]
    public async Task OtherRoute_KeepsPassThroughClaims_ButStripsReservedOnes_AndSkipsTheMapper()
    {
        var mapper = new Mock<IMcpCallerMapper>(MockBehavior.Strict);
        var token = MintToken(
            DelegatedClaims(),
            new Claim("allowed_projects", "[99]"),
            new Claim("scope", "Admin"),
            new Claim("region", "eu"));

        var (_, user) = await RunAsync("/beacon/api/projects", token, mapperOverride: mapper.Object);

        user.FindFirst("region")!.Value.Should().Be("eu");
        user.FindFirst("allowed_projects").Should().BeNull();
        user.FindFirst("scope").Should().BeNull();
        user.FindAll("auth_method").Select(x => x.Value).Should().Equal("jwt");
        user.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("pairwise-sub", "non-MCP identity is unchanged");
        (await AuthorizeExecuteAsync(user)).Should().BeTrue("JWT callers outside MCP are not scope-gated");
    }

    private static async Task<(HttpContext Context, ClaimsPrincipal User)> RunAsync(
        string path,
        string token,
        McpCallerOptions? options = null,
        IUserManagementService? userService = null,
        IMcpCallerMapper? mapperOverride = null)
    {
        var jwtOptions = new JwtAuthenticationOptions
        {
            EnableBearerAuthentication = true,
            Validation = new JwtValidationOptions
            {
                SigningKey = SigningKey,
                ValidIssuer = Issuer,
                ValidAudience = Audience
            }
        };
        var provider = new JwtExternalApiAuthenticationProvider(
            new HttpClient(),
            jwtOptions,
            NullLogger<JwtExternalApiAuthenticationProvider>.Instance);
        var mapper = mapperOverride ?? new ConfiguredMcpCallerMapper(
            Options.Create(options ?? Callers),
            new MemoryCache(new MemoryCacheOptions()),
            Hasher,
            NullLogger<ConfiguredMcpCallerMapper>.Instance,
            userService);

        ClaimsPrincipal? seen = null;
        var middleware = new JwtBearerAuthMiddleware(
            x =>
            {
                seen = x.User;
                return Task.CompletedTask;
            },
            jwtOptions,
            NullLogger<JwtBearerAuthMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Headers.Authorization = $"Bearer {token}";

        await middleware.InvokeAsync(context, provider, mapper);

        seen.Should().NotBeNull("a valid token must reach the next middleware");
        return (context, seen!);
    }

    private static string MintToken(IEnumerable<Claim> claims, params Claim[] extra)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            Issuer,
            Audience,
            claims.Concat(extra),
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static List<Claim> AppOnlyClaims(string appId)
    {
        return
        [
            new Claim("sub", "sp-oid"),
            new Claim("oid", "sp-oid"),
            new Claim("tid", TenantId),
            new Claim("azp", appId),
            new Claim("idtyp", "app"),
            new Claim("roles", "Mcp.System")
        ];
    }

    private static List<Claim> DelegatedClaims()
    {
        return
        [
            new Claim("sub", "pairwise-sub"),
            new Claim("oid", UserOid),
            new Claim("tid", TenantId),
            new Claim("azp", AiProxyClientId),
            new Claim("scp", "Mcp.Access"),
            new Claim("preferred_username", "ana@contoso.example"),
            new Claim("name", "Ana Analyst")
        ];
    }

    private static IProjectContext CreateProjectContext(HttpContext context)
    {
        var services = new ServiceCollection();
        services.AddScoped<McpProjectContext>();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        return ProjectContextFactory.Create(scope.ServiceProvider);
    }

    private static async Task<bool> AuthorizeExecuteAsync(ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBeaconApiAuthorization();
        using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        var result = await authorization.AuthorizeAsync(user, resource: null, BeaconApiEndpoints.ExecuteScopePolicyName);

        return result.Succeeded;
    }
}
