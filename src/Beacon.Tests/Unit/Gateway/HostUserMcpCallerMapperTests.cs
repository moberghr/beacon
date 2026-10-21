using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authentication;
using Beacon.Core.HostData;
using Beacon.Core.Mcp;

namespace Beacon.Tests.Unit.Gateway;

/// <summary>
/// The host's users decide who may use MCP: a delegated token of the accepted tenant and audience, with the MCP scope,
/// from an allowed client, whose person the host knows, once the host project is synced — and nothing else. The tool call
/// runs as that host user, resolved again every time, on an identity the host recognises as an MCP call.
/// </summary>
[TestFixture]
public class HostUserMcpCallerMapperTests
{
    private const string TenantId = "11111111-2222-3333-4444-555555555555";
    private const string ClientId = "66666666-7777-8888-9999-000000000000";
    private const string ClaudeClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string ObjectId = "0000000a-0000-0000-0000-000000000001";
    private static readonly string EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private Mock<IMcpHostUserResolver> _users = null!;
    private Mock<IBeaconHostProjectGate> _hostProject = null!;

    [SetUp]
    public void SetUp()
    {
        _users = new Mock<IMcpHostUserResolver>();
        _users
            .Setup(x => x.FindAsync(It.Is<McpTokenIdentity>(y => y.ObjectId == ObjectId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpHostUser("42", "Ada Lovelace", [new Claim("app_user_id", "42")]));
        _hostProject = new Mock<IBeaconHostProjectGate>();
        _hostProject.Setup(x => x.GetProjectIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(7);
    }

    [Test]
    public async Task ADelegatedTokenOfAKnownPerson_IsAUserCallerOfTheHostProject_WithExecute()
    {
        var caller = await Mapper().MapAsync(Token(), CancellationToken.None);

        caller.Should().NotBeNull();
        caller!.Kind.Should().Be(McpCallerKind.User);
        caller.Name.Should().Be("Ada Lovelace");
        caller.AllowedProjectIds.Should().Equal(7);
        caller.Scope.Should().Be(McpCallerScope.Execute);
        caller.BeaconUserId.Should().BeNull("no Beacon user is provisioned");
        caller.SubjectHash.Should().Be(new McpCallerSubjectHasher(EncryptionKey).Hash(TenantId, ObjectId));
        _users.Verify(x => x.FindAsync(
            It.Is<McpTokenIdentity>(y => y.TenantId == TenantId && y.ClientId == ClaudeClientId && y.Scopes.SequenceEqual(new[] { "mcp.access", "User.Read" })),
            It.IsAny<CancellationToken>()));
    }

    [TestCaseSource(nameof(RefusedTokens))]
    public async Task ATokenThatIsNotAnAcceptedCaller_IsRefused(string because, ClaimsPrincipal token)
    {
        var caller = await Mapper().MapAsync(token, CancellationToken.None);

        caller.Should().BeNull(because);
    }

    [Test]
    public async Task AClientOutsideTheAllowList_IsRefused_AndOneOnItIsAccepted()
    {
        var refused = await Mapper(allowedClients: ["ffffffff-0000-0000-0000-000000000000"]).MapAsync(Token(), CancellationToken.None);
        var accepted = await Mapper(allowedClients: [ClaudeClientId]).MapAsync(Token(), CancellationToken.None);

        refused.Should().BeNull();
        accepted.Should().NotBeNull();
    }

    [Test]
    public async Task APersonTheHostDoesNotKnow_IsRefused()
    {
        var caller = await Mapper().MapAsync(Token(objectId: "0000000a-0000-0000-0000-000000000999"), CancellationToken.None);

        caller.Should().BeNull();
    }

    [Test]
    public async Task EveryoneIsRefused_WhileTheHostProjectIsNotSynced()
    {
        _hostProject.Setup(x => x.GetProjectIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((int?)null);

        var caller = await Mapper().MapAsync(Token(), CancellationToken.None);

        caller.Should().BeNull();
    }

    [Test]
    public async Task AToolCall_RunsAsTheHostUser_ResolvedAgain_OnAnIdentityTheHostRecognises()
    {
        var caller = (await Mapper().MapAsync(Token(), CancellationToken.None))!;
        await using var services = new ServiceCollection().AddScoped(_ => _users.Object).BuildServiceProvider();

        var principal = await new HostUserMcpHostPrincipalFactory(services.GetRequiredService<IServiceScopeFactory>())
            .CreateAsync(caller, new ClaimsPrincipal(), CancellationToken.None);

        principal.Should().NotBeNull();
        principal!.FindFirst("app_user_id")!.Value.Should().Be("42");
        principal.Identity!.Name.Should().Be("Ada Lovelace");
        principal.IsBeaconMcpCall().Should().BeTrue();
        principal.Claims.Should().NotContain(x => x.Type.StartsWith("beacon:mcp_host_user:"), "what the mapper carried never reaches the host");
        _users.Verify(x => x.FindAsync(It.IsAny<McpTokenIdentity>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task AToolCallOfAPersonTheHostNoLongerKnows_IsRefused()
    {
        var caller = (await Mapper().MapAsync(Token(), CancellationToken.None))!;
        _users.Reset();
        await using var services = new ServiceCollection().AddScoped(_ => _users.Object).BuildServiceProvider();

        var principal = await new HostUserMcpHostPrincipalFactory(services.GetRequiredService<IServiceScopeFactory>())
            .CreateAsync(caller, new ClaimsPrincipal(), CancellationToken.None);

        principal.Should().BeNull("a deactivated account loses MCP on its next call");
    }

    [Test]
    public async Task ASystemCaller_GetsNoHostPrincipal()
    {
        await using var services = new ServiceCollection().AddScoped(_ => _users.Object).BuildServiceProvider();
        var system = new McpCaller(McpCallerKind.System, "bot", "hash", null, [7], McpCallerScope.Execute, []);

        var principal = await new HostUserMcpHostPrincipalFactory(services.GetRequiredService<IServiceScopeFactory>())
            .CreateAsync(system, new ClaimsPrincipal(), CancellationToken.None);

        principal.Should().BeNull();
    }

    [Test]
    public void ABrowserSessionOrAPlainPrincipal_IsNotAnMcpCall()
    {
        var session = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Ada")], "Cookies"));

        session.IsBeaconMcpCall().Should().BeFalse();
        new ClaimsPrincipal(new ClaimsIdentity()).IsBeaconMcpCall().Should().BeFalse();
    }

    private static IEnumerable<TestCaseData> RefusedTokens()
    {
        yield return new TestCaseData("another tenant", Token(tenantId: "99999999-2222-3333-4444-555555555555"));
        yield return new TestCaseData("another audience", Token(audience: "api://someone-else"));
        yield return new TestCaseData("no oid", Token(objectId: null));
        yield return new TestCaseData("an ID token", Token(extra: [new Claim("nonce", "n")]));
        yield return new TestCaseData("an app-only token", Token(scope: null, extra: [new Claim("idtyp", "app"), new Claim("roles", "Tools.Call")]));
        yield return new TestCaseData("another scope of the registration", Token(scope: "files.read"));
    }

    private HostUserMcpCallerMapper Mapper(List<string>? allowedClients = null)
    {
        var jwt = new JwtAuthenticationOptions();
        jwt.Validation.ValidAudiences = [$"api://{ClientId}", ClientId];

        return new HostUserMcpCallerMapper(
            _users.Object,
            _hostProject.Object,
            new McpCallerSubjectHasher(EncryptionKey),
            Options.Create(new HostUserMcpCallerOptions
            {
                TenantId = TenantId,
                ProjectName = "inventhor",
                AllowedClientIds = allowedClients ?? []
            }),
            NullLogger<HostUserMcpCallerMapper>.Instance,
            jwt);
    }

    private static ClaimsPrincipal Token(
        string? tenantId = TenantId,
        string? audience = ClientId,
        string? objectId = ObjectId,
        string? scope = "mcp.access User.Read",
        Claim[]? extra = null)
    {
        var claims = new List<Claim> { new("azp", ClaudeClientId) };
        void Add(string type, string? value)
        {
            if (value != null)
            {
                claims.Add(new Claim(type, value));
            }
        }

        Add("tid", tenantId);
        Add("aud", audience);
        Add("oid", objectId);
        Add("sub", "pairwise-subject");
        Add("scp", scope);
        claims.AddRange(extra ?? []);

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }
}
