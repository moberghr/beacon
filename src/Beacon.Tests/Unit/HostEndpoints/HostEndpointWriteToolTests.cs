using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Beacon.Core.HostDocs;
using Beacon.Core.HostEndpoints;
using Beacon.Core.Mcp;
using Beacon.MCP.HostEndpoints;
using static Beacon.Tests.Unit.HostEndpoints.HostEndpointDispatchTests;

namespace Beacon.Tests.Unit.HostEndpoints;

/// <summary>
/// Write tools: an endpoint declared <c>ReadOnly = false</c> is exposed only with <c>AllowWriteTools</c>, carries the
/// declared (not verb-inferred) annotations, runs for user callers only and is audited as <c>api_&lt;name&gt;:write</c>.
/// </summary>
[TestFixture]
public class HostEndpointWriteToolTests
{
    private HostEndpointTestHost _host = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await HostEndpointTestHost.StartAsync(
            [typeof(ThingsController), typeof(LeaveController)],
            configure: x => x.AllowWriteTools = true);
    }

    [OneTimeTearDown]
    public async Task StopHost()
    {
        await _host.DisposeAsync();
    }

    [SetUp]
    public void ClearAudit()
    {
        lock (_host.AuditLogs)
        {
            _host.AuditLogs.Clear();
        }
    }

    [Test]
    public async Task Startup_RefusesAWriteTool_WithoutTheOptIn()
    {
        var start = () => HostEndpointTestHost.StartAsync([typeof(LeaveController)], mapDefaultMinimalApis: false);

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("[leave_book]").And.Contain("[leave_withdraw]").And.Contain("HostEndpointToolOptions.AllowWriteTools");
    }

    [Test]
    public async Task Startup_StillRefusesAnUndeclaredReadOnlyFlag_WithTheOptIn()
    {
        var start = () => HostEndpointTestHost.StartAsync(
            [typeof(MissingReadOnlyController)],
            mapDefaultMinimalApis: false,
            configure: x => x.AllowWriteTools = true);

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("missing_readonly").And.Contain("ReadOnly must be declared explicitly").And.Contain("never infers it from the HTTP verb");
    }

    [Test]
    public void Startup_AcceptsWriteTools_WithTheOptIn()
    {
        _host.Tool("leave_book").ReadOnly.Should().BeFalse();
        _host.Tool("leave_withdraw").ReadOnly.Should().BeFalse();
        _host.Tool("thing_by_id").ReadOnly.Should().BeTrue();
        _host.Tool("leave_book").Description.Should().Contain("Write host endpoint POST /leave/book").And.Contain("only when the user asked");
    }

    [Test]
    public void Annotations_ComeFromTheDeclaration_NotTheVerb()
    {
        var read = _host.Tool("thing_search").ToProtocolTool().Annotations!;
        var book = _host.Tool("leave_book").ToProtocolTool().Annotations!;
        var withdraw = _host.Tool("leave_withdraw").ToProtocolTool().Annotations!;

        // thing_search reads over POST: still read-only, because it is declared so.
        read.ReadOnlyHint.Should().BeTrue();
        read.DestructiveHint.Should().BeFalse();
        read.IdempotentHint.Should().BeTrue();
        book.ReadOnlyHint.Should().BeFalse();
        book.DestructiveHint.Should().BeFalse();
        book.IdempotentHint.Should().BeFalse();
        book.OpenWorldHint.Should().BeFalse();
        withdraw.ReadOnlyHint.Should().BeFalse();
        withdraw.DestructiveHint.Should().BeTrue();
        withdraw.IdempotentHint.Should().BeTrue();
        withdraw.OpenWorldHint.Should().BeFalse();
    }

    [Test]
    public async Task AMinimalApiWriteTool_CarriesItsDeclaredHints()
    {
        await using var host = await HostEndpointTestHost.StartAsync(
            [],
            mapDefaultMinimalApis: false,
            configure: x => x.AllowWriteTools = true,
            map: x => x.MapDelete("/minimal/leave/{id:int}", (int id) => Results.Ok(new { id }))
                .RequireAuthorization(HostEndpointTestHost.ViewThingPolicy)
                .WithBeaconTool("minimal_leave_delete", "Delete a leave request.", readOnly: false, destructive: true, idempotent: true));

        var annotations = host.Tool("minimal_leave_delete").ToProtocolTool().Annotations!;

        annotations.ReadOnlyHint.Should().BeFalse();
        annotations.DestructiveHint.Should().BeTrue();
        annotations.IdempotentHint.Should().BeTrue();
    }

    [Test]
    public async Task AUserCaller_SeesAndCallsAWriteTool_AuditedAsAWrite()
    {
        var service = _host.CreateService(HostEndpointTestHost.OuterRequest(UserWithLeave()));

        var tools = await service.ListToolsAsync(CancellationToken.None);
        var result = await service.CallAsync("api_leave_book", Args(new { from = "2026-12-21", to = "2026-12-24" }), CancellationToken.None);
        await service.CallAsync("api_thing_by_id", Args(new { id = 1 }), CancellationToken.None);

        tools.Select(x => x.Name).Should().Contain(["api_leave_book", "api_leave_withdraw", "api_thing_by_id"]);
        tools.Single(x => x.Name == "api_leave_book").Annotations!.ReadOnlyHint.Should().BeFalse();
        result.IsError.Should().NotBe(true, Text(result));
        result.StructuredContent!.Value.GetProperty("from").GetString().Should().Be("2026-12-21");
        result.StructuredContent!.Value.GetProperty("user").GetString().Should().Be("Ada Lovelace");
        _host.AuditLogs.Select(x => x.Tool).Should().Equal("api_leave_book:write", "api_thing_by_id");
        _host.AuditLogs[0].CallerKind.Should().Be("User");
        _host.AuditLogs[0].ErrorMessage.Should().BeNull();
    }

    [Test]
    public async Task ProjectOverview_ListsWhatTheCallersToolListOffers()
    {
        var systemCaller = HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings));

        // One service per call: every HttpContextAccessor shares one async-local request, so the latest CreateService wins.
        var forUser = await _host.CreateService(HostEndpointTestHost.OuterRequest(UserWithLeave()))
            .ListForProjectAsync(HostEndpointTestHost.ProjectId, CancellationToken.None);
        var forAnotherProject = await _host.CreateService(HostEndpointTestHost.OuterRequest(UserWithLeave()))
            .ListForProjectAsync(HostEndpointTestHost.ProjectId + 1, CancellationToken.None);
        var forSystem = await _host.CreateService(HostEndpointTestHost.OuterRequest(systemCaller))
            .ListForProjectAsync(HostEndpointTestHost.ProjectId, CancellationToken.None);
        var forApiKey = await _host.CreateService(HostEndpointTestHost.OuterRequest(null))
            .ListForProjectAsync(HostEndpointTestHost.ProjectId, CancellationToken.None);

        forUser!.Tools.Should().Contain(x => x.ToolName == "api_leave_book" && !x.ReadOnly);
        forSystem!.Tools.Select(x => x.ToolName).Should().Contain("api_thing_by_id").And.NotContain(x => x.StartsWith("api_leave_"));
        forApiKey.Should().BeNull("a credential that cannot call the tools is not shown them");
        forAnotherProject.Should().BeNull("the tools belong to one project");
    }

    [Test]
    public async Task ASystemCaller_NeitherSeesNorCallsAWriteTool_EvenWithTheHostPermission()
    {
        var caller = HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings));
        var service = _host.CreateService(HostEndpointTestHost.OuterRequest(caller));
        var bookings = LeaveController.Bookings;

        var tools = await service.ListToolsAsync(CancellationToken.None);
        var result = await service.CallAsync("api_leave_book", Args(new { from = "2026-12-21", to = "2026-12-24" }), CancellationToken.None);

        tools.Select(x => x.Name).Should().Contain("api_thing_by_id").And.NotContain(x => x.StartsWith("api_leave_"));
        result.IsError.Should().BeTrue();
        Text(result).Should().Be("Forbidden: api_leave_book changes data in the host application, and write tools run only for a signed-in user; a system caller cannot call them.");
        LeaveController.Bookings.Should().Be(bookings, "the endpoint never runs");
        var row = _host.AuditLogs.Should().ContainSingle().Subject;
        row.Tool.Should().Be("api_leave_book:write");
        row.CallerKind.Should().Be("System");
        row.ErrorMessage.Should().StartWith("Forbidden");
    }

    [Test]
    public async Task CatalogMode_ForAUser_CallApiIsNotReadOnly_AndSearchFlagsWrites()
    {
        await using var host = await StartCatalogHostAsync();
        var service = host.CreateService(HostEndpointTestHost.OuterRequest(UserWithLeave()));

        var tools = await service.ListToolsAsync(CancellationToken.None);
        var search = await service.CallAsync("search_api", Args(new { query = "leave", limit = 50 }), CancellationToken.None);
        var call = await service.CallAsync("call_api", Args(new { name = "leave_book", arguments = new { from = "2026-12-21", to = "2026-12-24" } }), CancellationToken.None);

        tools.Select(x => x.Name).Should().Equal("search_api", "call_api");
        tools[0].Annotations!.ReadOnlyHint.Should().BeTrue("searching changes nothing");
        var callApi = tools[1].Annotations!;
        callApi.ReadOnlyHint.Should().BeFalse("call_api reaches write tools");
        callApi.DestructiveHint.Should().BeTrue("leave_withdraw is destructive");
        callApi.IdempotentHint.Should().BeFalse();
        tools[1].Description.Should().Contain("read_only: false");

        var found = search.StructuredContent!.Value.GetProperty("tools").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);
        found["leave_book"].GetProperty("read_only").GetBoolean().Should().BeFalse();
        found["leave_book"].GetProperty("destructive").GetBoolean().Should().BeFalse();
        found["leave_withdraw"].GetProperty("destructive").GetBoolean().Should().BeTrue();
        found["leave_withdraw"].GetProperty("idempotent").GetBoolean().Should().BeTrue();

        var reads = await service.CallAsync("search_api", Args(new { query = "thing", limit = 50 }), CancellationToken.None);
        var readResults = reads.StructuredContent!.Value.GetProperty("tools").EnumerateArray().ToList();
        readResults.Should().NotBeEmpty();
        readResults.Select(x => x.GetProperty("read_only").GetBoolean()).Should().OnlyContain(x => x);
        readResults.Select(x => x.TryGetProperty("destructive", out var _)).Should().OnlyContain(x => !x, "the write hints are only sent for write tools");

        call.IsError.Should().NotBe(true, Text(call));
        host.AuditLogs.Select(x => x.Tool).Should().Equal("search_api", "api_leave_book:write", "search_api");
    }

    [Test]
    public async Task CatalogMode_ForASystemCaller_HidesWrites_AndCallApiRefusesThem()
    {
        await using var host = await StartCatalogHostAsync();
        var caller = HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings));
        var service = host.CreateService(HostEndpointTestHost.OuterRequest(caller));

        var tools = await service.ListToolsAsync(CancellationToken.None);
        var search = await service.CallAsync("search_api", Args(new { limit = 50 }), CancellationToken.None);
        var call = await service.CallAsync("call_api", Args(new { name = "leave_withdraw", arguments = new { id = 4 } }), CancellationToken.None);

        tools.Should().OnlyContain(x => x.Annotations!.ReadOnlyHint == true, "no write tool is reachable for this caller");
        var payload = search.StructuredContent!.Value;
        payload.GetProperty("tools").EnumerateArray().Select(x => x.GetProperty("name").GetString()).Should().NotContain(x => x!.StartsWith("leave_"));
        payload.GetProperty("total_tools").GetInt32().Should().Be(host.Registry.Tools.Count(x => x.ReadOnly));
        call.IsError.Should().BeTrue();
        Text(call).Should().Contain("a system caller cannot call them");
        host.AuditLogs.Select(x => x.Tool).Should().Equal("search_api", "api_leave_withdraw:write");
    }

    [Test]
    public void WriteTools_AreVisibleToUserCallersOnly()
    {
        HostEndpointToolService.IsVisibleTo(_host.Tool("thing_by_id"), HostEndpointTestHost.SystemCaller()).Should().BeTrue();
        HostEndpointToolService.IsVisibleTo(_host.Tool("leave_book"), HostEndpointTestHost.SystemCaller()).Should().BeFalse();
        HostEndpointToolService.IsVisibleTo(_host.Tool("leave_book"), HostEndpointTestHost.UserCaller()).Should().BeTrue();
    }

    [Test]
    public async Task TheBrief_MarksWriteTools()
    {
        var catalog = (IHostEndpointToolCatalog)_host.App.Services.GetService(typeof(IHostEndpointToolCatalog))!;
        var listing = await catalog.GetForProjectAsync(HostEndpointTestHost.ProjectId, CancellationToken.None);

        var brief = ProjectBriefService.Render(new ProjectBriefData("Netgiro", null, [], [], 0, [], [], 0, [], 0, listing));

        listing!.Tools.Single(x => x.Name == "leave_book").ReadOnly.Should().BeFalse();
        listing.Tools.Single(x => x.Name == "thing_by_id").ReadOnly.Should().BeTrue();
        brief.Should().Contain("- `api_leave_book` *(writes)* — Book leave for yourself.")
            .And.Contain("- `api_thing_by_id` — One thing")
            .And.Contain("call one only when the user asked for that change");
    }

    private static McpCaller UserWithLeave() =>
        HostEndpointTestHost.UserCaller(null, HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings));

    private static Task<HostEndpointTestHost> StartCatalogHostAsync() =>
        HostEndpointTestHost.StartAsync(
            [typeof(ThingsController), typeof(LeaveController)],
            configure: x =>
            {
                x.AllowWriteTools = true;
                x.NamedToolLimit = 1;
            });
}
