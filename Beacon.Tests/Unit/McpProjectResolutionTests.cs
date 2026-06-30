using FluentAssertions;
using NUnit.Framework;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;

namespace Beacon.Tests.Unit;

/// <summary>
/// Guards the data-isolation fix in <see cref="ToolHelper.ResolveProjectId"/>: an explicit
/// per-call project_id must resolve into request-scoped context only and must NOT be written
/// to the shared (user+apiKey) session state, or a concurrent call on the same key that omits
/// project_id would inherit it and execute against the wrong project.
/// </summary>
[TestFixture]
public class McpProjectResolutionTests
{
    private McpProjectContextManager _sessions = null!;

    [SetUp]
    public void SetUp() => _sessions = new McpProjectContextManager();

    private static McpProjectContext MultiProjectKey() =>
        new() { UserId = 1, ApiKeyId = 9, AllowedProjectIds = [5, 6, 7] };

    [Test]
    public void ExplicitProjectId_DoesNotLeakToConcurrentNoArgCall_OnSameKey()
    {
        // Call A selects project 5 explicitly.
        var ctxA = MultiProjectKey();
        var errA = ToolHelper.ResolveProjectId(ctxA, _sessions, requestedProjectId: 5, out var projectIdA);
        errA.Should().BeNull();
        projectIdA.Should().Be(5);
        ctxA.ActiveProjectId.Should().Be(5);

        // A concurrent call B on the SAME api key omits project_id. It must NOT inherit A's choice.
        var ctxB = MultiProjectKey();
        var errB = ToolHelper.ResolveProjectId(ctxB, _sessions, requestedProjectId: null, out var projectIdB);

        errB.Should().Contain("Specify project_id");
        projectIdB.Should().Be(0);
        projectIdB.Should().NotBe(5);

        // The explicit selection must never have touched shared session state.
        var key = McpProjectContextManager.MakeKey(1, 9);
        _sessions.GetOrCreate(key).ActiveProjectId.Should().BeNull();
    }

    [Test]
    public void ExplicitProjectId_NotInAllowedList_IsDenied()
    {
        var ctx = MultiProjectKey();

        var error = ToolHelper.ResolveProjectId(ctx, _sessions, requestedProjectId: 99, out var projectId);

        error.Should().Contain("Access denied");
        projectId.Should().Be(0);
    }

    [Test]
    public void SingleProjectKey_NoArg_ResolvesAndBecomesSticky()
    {
        var ctx = new McpProjectContext { UserId = 2, ApiKeyId = 3, AllowedProjectIds = [42] };

        var error = ToolHelper.ResolveProjectId(ctx, _sessions, requestedProjectId: null, out var projectId);

        error.Should().BeNull();
        projectId.Should().Be(42);

        // Single-project auto-resolution is the only writer of sticky state — unambiguous, safe.
        var key = McpProjectContextManager.MakeKey(2, 3);
        _sessions.GetOrCreate(key).ActiveProjectId.Should().Be(42);
    }

    [Test]
    public void NoAllowedProjects_ReturnsActionableError()
    {
        var ctx = new McpProjectContext { UserId = 4, ApiKeyId = 5, AllowedProjectIds = [] };

        var error = ToolHelper.ResolveProjectId(ctx, _sessions, requestedProjectId: null, out var projectId);

        error.Should().Contain("No projects");
        projectId.Should().Be(0);
    }
}
