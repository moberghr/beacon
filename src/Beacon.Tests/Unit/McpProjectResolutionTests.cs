using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Guards the stateless design of <see cref="ToolHelper.ResolveProjectId"/>: project resolution is
/// a pure per-call function of the request-scoped context and the explicit project_id parameter.
/// There is no cross-call session state — a project selected on one call must never influence a
/// concurrent or subsequent call, so any instance behind a load balancer resolves identically.
/// </summary>
[TestFixture]
public class McpProjectResolutionTests
{
    private static McpProjectContext MultiProjectContext() =>
        new() { UserId = 1, ApiKeyId = 9, AllowedProjectIds = [5, 6, 7] };

    [Test]
    public void ExplicitProjectId_InAllowedList_Resolves()
    {
        var ctx = MultiProjectContext();

        var error = ToolHelper.ResolveProjectId(ctx, "get_context", requestedProjectId: 5, out var projectId);

        error.Should().BeNull();
        projectId.Should().Be(5);
        ctx.ActiveProjectId.Should().Be(5);
    }

    [Test]
    public void ExplicitProjectId_NullAllowedList_IsDenied_FailClosed()
    {
        // A context nobody filled (no caller resolved) reaches no project; it is never read as unrestricted.
        var ctx = new McpProjectContext { UserId = 1, ApiKeyId = 9, AllowedProjectIds = null };

        var error = ToolHelper.ResolveProjectId(ctx, "get_context", requestedProjectId: 12, out var projectId);

        error.Should().Be("Access denied: you do not have access to project 12.");
        projectId.Should().Be(0);
        ctx.ActiveProjectId.Should().BeNull();
    }

    [Test]
    public void ExplicitProjectId_NotInAllowedList_IsDenied()
    {
        var ctx = MultiProjectContext();

        var error = ToolHelper.ResolveProjectId(ctx, "get_context", requestedProjectId: 99, out var projectId);

        error.Should().Contain("Access denied");
        projectId.Should().Be(0);
    }

    [Test]
    public void ExplicitProjectId_EmptyAllowedList_IsDenied_FailClosed()
    {
        var ctx = new McpProjectContext { UserId = 1, ApiKeyId = 9, AllowedProjectIds = [] };

        var error = ToolHelper.ResolveProjectId(ctx, "get_context", requestedProjectId: 5, out var projectId);

        error.Should().Contain("Access denied");
        projectId.Should().Be(0);
    }

    [Test]
    public void SingleProject_NoArg_AutoResolves()
    {
        var ctx = new McpProjectContext { UserId = 2, ApiKeyId = 3, AllowedProjectIds = [42] };

        var error = ToolHelper.ResolveProjectId(ctx, "get_context", requestedProjectId: null, out var projectId);

        error.Should().BeNull();
        projectId.Should().Be(42);
        ctx.ActiveProjectId.Should().Be(42);
    }

    [Test]
    public void MultiProject_NoArg_ReturnsErrorListingIds()
    {
        var ctx = MultiProjectContext();

        var error = ToolHelper.ResolveProjectId(ctx, "get_context", requestedProjectId: null, out var projectId);

        error.Should().Contain("Multiple projects available");
        error.Should().Contain("5, 6, 7");
        error.Should().Contain("Specify project_id");
        projectId.Should().Be(0);
    }

    [Test]
    public void NoAllowedProjects_ReturnsActionableError()
    {
        var ctx = new McpProjectContext { UserId = 4, ApiKeyId = 5, AllowedProjectIds = [] };

        var error = ToolHelper.ResolveProjectId(ctx, "get_context", requestedProjectId: null, out var projectId);

        error.Should().Contain("No projects");
        projectId.Should().Be(0);
    }

    [Test]
    public void NullAllowedProjects_NoArg_ReturnsActionableError()
    {
        var ctx = new McpProjectContext { UserId = 4, ApiKeyId = 5, AllowedProjectIds = null };

        var error = ToolHelper.ResolveProjectId(ctx, "get_context", requestedProjectId: null, out var projectId);

        error.Should().Contain("No projects");
        projectId.Should().Be(0);
    }

    [Test]
    public void ExplicitProjectId_DoesNotLeakToSubsequentNoArgCall()
    {
        // Call A selects project 5 explicitly.
        var ctxA = MultiProjectContext();
        var errA = ToolHelper.ResolveProjectId(ctxA, "get_context", requestedProjectId: 5, out var projectIdA);
        errA.Should().BeNull();
        projectIdA.Should().Be(5);

        // Call B on the SAME api key omits project_id. Resolution is pure per-call —
        // it must NOT inherit A's choice through any shared state.
        var ctxB = MultiProjectContext();
        var errB = ToolHelper.ResolveProjectId(ctxB, "get_context", requestedProjectId: null, out var projectIdB);

        errB.Should().Contain("Specify project_id");
        projectIdB.Should().Be(0);
        ctxB.ActiveProjectId.Should().BeNull();
    }

    // --- The context is filled from the caller's project access, and fails closed ---

    private static readonly Claim ApiKeyMarker = new("auth_method", "api_key");

    private static IServiceProvider BuildProvider(ClaimsPrincipal? user, IProjectAccessEvaluator? evaluator = null, int beaconUserId = 4)
    {
        var services = new ServiceCollection();
        services.AddScoped<McpProjectContext>();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = user == null ? null : new DefaultHttpContext { User = user } });
        services.AddSingleton(evaluator ?? TestProjectAccess.ForPrincipal(user));
        services.AddSingleton(TestProjectAccess.Actors(beaconUserId: beaconUserId));

        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "test"));

    [Test]
    public void Create_BeforeTheContextIsFilled_ReachesNoProject()
    {
        var ctx = ProjectContextFactory.Create(BuildProvider(Principal(ApiKeyMarker, new Claim("allowed_projects", "[5]"))));

        ToolHelper.ResolveProjectId(ctx, "get_context", requestedProjectId: 5, out _).Should().StartWith("Access denied");
    }

    [TestCase("api_key")]
    [TestCase("mcp_caller")]
    public async Task Populate_AScopedCaller_GetsTheProjectsItsAccessReads_AndItsKeyId(string authMethod)
    {
        var provider = BuildProvider(
            Principal(new Claim("auth_method", authMethod), new Claim("allowed_projects", "[5,6,7]"), new Claim("api_key_id", "12")),
            TestProjectAccess.Evaluator(TestProjectAccess.ReadableIn(5, 7)));

        await ProjectContextFactory.PopulateAsync(provider, CancellationToken.None);

        var ctx = provider.GetRequiredService<McpProjectContext>();
        ctx.AllowedProjectIds.Should().Equal([5, 7], "the projects come from the caller's project access, not from its claim");
        ctx.ApiKeyId.Should().Be(12);
        ctx.UserId.Should().Be(4, "the caller is recorded by its Beacon user");
    }

    [TestCase(null)]
    [TestCase("jwt")]
    public async Task Populate_ProjectsAndKeyIdOnAnyOtherPrincipal_AreIgnored(string? authMethod)
    {
        // A browser session or REST bearer token carrying these claim types got them from elsewhere (an identity
        // provider, a claims transformation): it gets no projects and no key id, whatever its project access.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "ext-4"),
            new("allowed_projects", "[5,6,7]"),
            new("api_key_id", "12")
        };
        if (authMethod != null)
        {
            claims.Add(new Claim("auth_method", authMethod));
        }

        var provider = BuildProvider(Principal([.. claims]), TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted));

        await ProjectContextFactory.PopulateAsync(provider, CancellationToken.None);

        var ctx = provider.GetRequiredService<McpProjectContext>();
        ctx.AllowedProjectIds.Should().NotBeNull().And.BeEmpty();
        ctx.ApiKeyId.Should().BeNull();
        ctx.UserId.Should().Be(4, "the session is recorded by its Beacon user, not by its NameIdentifier");
    }

    [Test]
    public async Task Populate_WithoutAnHttpRequest_ReachesNoProject()
    {
        var provider = BuildProvider(null, TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted));

        await ProjectContextFactory.PopulateAsync(provider, CancellationToken.None);

        var ctx = provider.GetRequiredService<McpProjectContext>();
        ctx.AllowedProjectIds.Should().NotBeNull().And.BeEmpty();
        ctx.UserId.Should().BeNull();
        ToolHelper.ResolveProjectId(ctx, "get_context", requestedProjectId: 5, out _).Should().StartWith("Access denied");
    }

    [Test]
    public async Task Populate_WhenProjectAccessCannotBeResolved_FailsClosed_WithoutThrowing()
    {
        var evaluator = new Mock<IProjectAccessEvaluator>();
        evaluator
            .Setup(x => x.GetScopeAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        var provider = BuildProvider(Principal(ApiKeyMarker, new Claim("allowed_projects", "[5]")), evaluator.Object);

        var act = () => ProjectContextFactory.PopulateAsync(provider, CancellationToken.None);

        await act.Should().NotThrowAsync("the call goes on to be refused and audited");
        provider.GetRequiredService<McpProjectContext>().AllowedProjectIds.Should().NotBeNull().And.BeEmpty();
    }
}
