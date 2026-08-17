using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;

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

        var error = ToolHelper.ResolveProjectId(ctx, requestedProjectId: 5, out var projectId);

        error.Should().BeNull();
        projectId.Should().Be(5);
        ctx.ActiveProjectId.Should().Be(5);
    }

    [Test]
    public void ExplicitProjectId_NullAllowedList_IsUnrestricted()
    {
        var ctx = new McpProjectContext { UserId = 1, ApiKeyId = 9, AllowedProjectIds = null };

        var error = ToolHelper.ResolveProjectId(ctx, requestedProjectId: 12, out var projectId);

        error.Should().BeNull();
        projectId.Should().Be(12);
        ctx.ActiveProjectId.Should().Be(12);
    }

    [Test]
    public void ExplicitProjectId_NotInAllowedList_IsDenied()
    {
        var ctx = MultiProjectContext();

        var error = ToolHelper.ResolveProjectId(ctx, requestedProjectId: 99, out var projectId);

        error.Should().Contain("Access denied");
        projectId.Should().Be(0);
    }

    [Test]
    public void ExplicitProjectId_EmptyAllowedList_IsDenied_FailClosed()
    {
        var ctx = new McpProjectContext { UserId = 1, ApiKeyId = 9, AllowedProjectIds = [] };

        var error = ToolHelper.ResolveProjectId(ctx, requestedProjectId: 5, out var projectId);

        error.Should().Contain("Access denied");
        projectId.Should().Be(0);
    }

    [Test]
    public void SingleProject_NoArg_AutoResolves()
    {
        var ctx = new McpProjectContext { UserId = 2, ApiKeyId = 3, AllowedProjectIds = [42] };

        var error = ToolHelper.ResolveProjectId(ctx, requestedProjectId: null, out var projectId);

        error.Should().BeNull();
        projectId.Should().Be(42);
        ctx.ActiveProjectId.Should().Be(42);
    }

    [Test]
    public void MultiProject_NoArg_ReturnsErrorListingIds()
    {
        var ctx = MultiProjectContext();

        var error = ToolHelper.ResolveProjectId(ctx, requestedProjectId: null, out var projectId);

        error.Should().Contain("Multiple projects available");
        error.Should().Contain("5, 6, 7");
        error.Should().Contain("Specify project_id");
        projectId.Should().Be(0);
    }

    [Test]
    public void NoAllowedProjects_ReturnsActionableError()
    {
        var ctx = new McpProjectContext { UserId = 4, ApiKeyId = 5, AllowedProjectIds = [] };

        var error = ToolHelper.ResolveProjectId(ctx, requestedProjectId: null, out var projectId);

        error.Should().Contain("No projects");
        projectId.Should().Be(0);
    }

    [Test]
    public void NullAllowedProjects_NoArg_ReturnsActionableError()
    {
        var ctx = new McpProjectContext { UserId = 4, ApiKeyId = 5, AllowedProjectIds = null };

        var error = ToolHelper.ResolveProjectId(ctx, requestedProjectId: null, out var projectId);

        error.Should().Contain("No projects");
        projectId.Should().Be(0);
    }

    [Test]
    public void ExplicitProjectId_DoesNotLeakToSubsequentNoArgCall()
    {
        // Call A selects project 5 explicitly.
        var ctxA = MultiProjectContext();
        var errA = ToolHelper.ResolveProjectId(ctxA, requestedProjectId: 5, out var projectIdA);
        errA.Should().BeNull();
        projectIdA.Should().Be(5);

        // Call B on the SAME api key omits project_id. Resolution is pure per-call —
        // it must NOT inherit A's choice through any shared state.
        var ctxB = MultiProjectContext();
        var errB = ToolHelper.ResolveProjectId(ctxB, requestedProjectId: null, out var projectIdB);

        errB.Should().Contain("Specify project_id");
        projectIdB.Should().Be(0);
        ctxB.ActiveProjectId.Should().BeNull();
    }

    // --- B7 fail-closed: ProjectContextFactory.Create must never produce a null AllowedProjectIds ---

    private static IServiceProvider BuildProvider(params Claim[] claims)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"))
        };

        var services = new ServiceCollection();
        services.AddSingleton<McpProjectContext>();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = httpContext });

        return services.BuildServiceProvider();
    }

    [Test]
    public void Create_AbsentAllowedProjectsClaim_FailsClosedToEmptyList()
    {
        var ctx = ProjectContextFactory.Create(BuildProvider(new Claim(ClaimTypes.NameIdentifier, "1")));

        ctx.AllowedProjectIds.Should().NotBeNull();
        ctx.AllowedProjectIds.Should().BeEmpty();
    }

    [Test]
    public void Create_EmptyJsonArrayClaim_FailsClosedToEmptyList()
    {
        var ctx = ProjectContextFactory.Create(BuildProvider(new Claim("allowed_projects", "[]")));

        ctx.AllowedProjectIds.Should().NotBeNull();
        ctx.AllowedProjectIds.Should().BeEmpty();
    }

    [Test]
    public void Create_MalformedJsonClaim_FailsClosedToEmptyList_NoThrow()
    {
        IProjectContext ctx = null!;

        var act = () => ctx = ProjectContextFactory.Create(BuildProvider(new Claim("allowed_projects", "{not json")));

        act.Should().NotThrow();
        ctx.AllowedProjectIds.Should().NotBeNull();
        ctx.AllowedProjectIds.Should().BeEmpty();
    }

    [Test]
    public void Create_ValidPopulatedClaim_IsParsed()
    {
        var ctx = ProjectContextFactory.Create(BuildProvider(new Claim("allowed_projects", "[5,6,7]")));

        ctx.AllowedProjectIds.Should().BeEquivalentTo(new[] { 5, 6, 7 });
    }
}
