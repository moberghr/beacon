using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using ModelContextProtocol.Protocol;
using Moq;
using NUnit.Framework;
using Beacon.Core.HostEndpoints;
using Beacon.Core.HostDocs;
using Beacon.Core.Mcp;
using Beacon.Core.Services.Retention;
using Beacon.MCP.HostEndpoints;

namespace Beacon.Tests.Unit.HostEndpoints;

[TestFixture]
public class HostEndpointDispatchTests
{
    private HostEndpointTestHost _host = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await HostEndpointTestHost.StartAsync();
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
    public async Task SystemCaller_WithHostClaims_RunsTheEndpointAsTheHostPrincipal()
    {
        var caller = HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings));
        var service = _host.CreateService(HostEndpointTestHost.OuterRequest(caller));

        var result = await service.CallAsync("api_thing_by_id", Args(new Dictionary<string, object?> { ["id"] = 2, ["include"] = "owner", ["X-Tenant"] = "t1" }), CancellationToken.None);

        result.IsError.Should().NotBe(true, Text(result));
        var body = result.StructuredContent!.Value;
        body.GetProperty("id").GetInt32().Should().Be(2);
        body.GetProperty("include").GetString().Should().Be("owner");
        body.GetProperty("tenant").GetString().Should().Be("t1");
        body.GetProperty("path").GetString().Should().Be("/things/2");
        body.GetProperty("user").GetString().Should().Be("routine-runner");
        body.GetProperty("authenticationType").GetString().Should().Be(DefaultMcpHostPrincipalFactory.SystemAuthenticationType);
        body.GetProperty("accessorUser").GetString().Should().Be("routine-runner", "host code reading IHttpContextAccessor sees the dispatched request");
        body.GetProperty("claims").EnumerateArray().Select(x => x.GetString()).Should().Contain("permission=things.view");
    }

    [Test]
    public async Task SystemCaller_WithoutTheHostPermission_IsForbidden_WithoutDetails()
    {
        var service = _host.CreateService(HostEndpointTestHost.OuterRequest(HostEndpointTestHost.SystemCaller()));

        var result = await service.CallAsync("api_thing_by_id", Args(new { id = 2 }), CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().Be("Forbidden.");
    }

    [Test]
    public async Task TheMcpCallersAuthorizationHeaderAndCookies_NeverReachTheHost()
    {
        var caller = HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings));
        var service = _host.CreateService(HostEndpointTestHost.OuterRequest(caller));

        var result = await service.CallAsync("api_thing_by_id", Args(new { id = 1 }), CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.GetProperty("authorization").GetString().Should().BeEmpty();
        body.GetProperty("cookie").GetString().Should().BeEmpty();
        Text(result).Should().NotContain("mcp-caller-token").And.NotContain("browser-session-cookie");
    }

    [Test]
    public async Task TheMcpRequestContext_SurvivesTheDispatch()
    {
        var caller = HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings));
        var outer = HostEndpointTestHost.OuterRequest(caller);
        var accessor = new HttpContextAccessor { HttpContext = outer };
        var service = _host.CreateService(outer);

        await service.CallAsync("api_thing_by_id", Args(new { id = 1 }), CancellationToken.None);

        accessor.HttpContext.Should().BeSameAs(outer, "the synthetic request runs in its own execution context");
        _host.AuditLogs.Should().ContainSingle().Which.CallerHash.Should().Be(HostEndpointTestHost.CallerHash);
    }

    [Test]
    public async Task DefaultUserPrincipal_CopiesIdentityClaims_AndNoBeaconClaims()
    {
        var caller = HostEndpointTestHost.UserCaller();

        var principal = await new DefaultMcpHostPrincipalFactory().CreateAsync(caller, HostEndpointTestHost.McpPrincipal(caller), CancellationToken.None);

        principal!.Identity!.AuthenticationType.Should().Be(DefaultMcpHostPrincipalFactory.UserAuthenticationType);
        principal.Identity.Name.Should().Be("Ada Lovelace");
        principal.Claims.Select(x => x.Type).Should().BeEquivalentTo(ClaimTypes.Name, ClaimTypes.Email, "oid");
        principal.IsInRole("Analyst").Should().BeFalse("Entra roles would grant host roles of the same name implicitly");
    }

    [Test]
    public async Task DefaultUserPrincipal_HasNoHostPermissions_SoPolicyEndpointsAreForbidden()
    {
        var service = _host.CreateService(HostEndpointTestHost.OuterRequest(HostEndpointTestHost.UserCaller()));

        var result = await service.CallAsync("api_thing_by_id", Args(new { id = 1 }), CancellationToken.None);

        Text(result).Should().Be("Forbidden.");
    }

    [Test]
    public async Task AHostPrincipalFactory_MapsTheUserToHostPermissions()
    {
        var factory = new Mock<IMcpHostPrincipalFactory>();
        factory
            .Setup(x => x.CreateAsync(It.IsAny<McpCaller>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "admin-ada"), HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewLoans)],
                "NetgiroAdmin")));
        var service = _host.CreateService(HostEndpointTestHost.OuterRequest(HostEndpointTestHost.UserCaller()), principalFactory: factory.Object);

        var result = await service.CallAsync("api_loan_grid", Args(new { CustomerId = 5 }), CancellationToken.None);

        result.IsError.Should().NotBe(true, Text(result));
        result.StructuredContent!.Value.GetProperty("customerId").GetInt32().Should().Be(5);
    }

    [Test]
    public async Task APrincipalFactoryThatReturnsNull_RefusesTheCall()
    {
        var factory = new Mock<IMcpHostPrincipalFactory>();
        var service = _host.CreateService(HostEndpointTestHost.OuterRequest(HostEndpointTestHost.UserCaller()), principalFactory: factory.Object);

        var result = await service.CallAsync("api_thing_by_id", Args(new { id = 1 }), CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().StartWith("Forbidden: the host application gives this caller no identity");
    }

    [Test]
    public async Task ACallerWithoutAMappedMcpCaller_IsRefused_AndSeesNoTools()
    {
        var service = _host.CreateService(HostEndpointTestHost.OuterRequest(null));

        var result = await service.CallAsync("api_thing_by_id", Args(new { id = 1 }), CancellationToken.None);
        var tools = await service.ListToolsAsync(CancellationToken.None);

        Text(result).Should().Contain("need a mapped Entra user or system caller");
        tools.Should().BeEmpty();
        _host.AuditLogs.Should().ContainSingle().Which.ErrorMessage.Should().Contain("Forbidden");
    }

    [TestCase(8, 7)]
    [TestCase(7, 8)]
    public async Task ACallerOutsideTheToolsProject_IsDenied_AndSeesNoTools(int callerProject, int contextProject)
    {
        var caller = HostEndpointTestHost.UserCaller([callerProject], HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings));
        var service = _host.CreateService(HostEndpointTestHost.OuterRequest(caller), contextProjects: [contextProject]);

        var result = await service.CallAsync("api_thing_by_id", Args(new { id = 1 }), CancellationToken.None);

        Text(result).Should().StartWith("Access denied");
        (await service.ListToolsAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Test]
    public async Task AMissingProject_MakesTheToolsUnavailable()
    {
        var resolver = new Mock<IHostEndpointProjectResolver>();
        resolver.Setup(x => x.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync((int?)null);
        var caller = HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings));
        var service = _host.CreateService(HostEndpointTestHost.OuterRequest(caller), projectResolver: resolver.Object);

        var result = await service.CallAsync("api_thing_by_id", Args(new { id = 1 }), CancellationToken.None);

        Text(result).Should().Contain("project 'Netgiro' does not exist");
        (await service.ListToolsAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Test]
    public async Task JsonBody_IsBound_AndATopLevelArrayBecomesStructuredItems()
    {
        var service = ThingsService();

        var result = await service.CallAsync("api_thing_search", Args(new { request = new { nameContains = "ea" } }), CancellationToken.None);

        result.IsError.Should().NotBe(true, Text(result));
        var items = result.StructuredContent!.Value.GetProperty("items");
        items.EnumerateArray().Select(x => x.GetProperty("name").GetString()).Should().Equal("Beacon");
    }

    [Test]
    public async Task MvcFormModel_WithValidateAntiForgeryToken_IsBound()
    {
        var caller = HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewLoans));
        var service = _host.CreateService(HostEndpointTestHost.OuterRequest(caller));

        var result = await service.CallAsync(
            "api_loan_grid",
            Args(new Dictionary<string, object?> { ["CustomerId"] = 42, ["Status"] = "Closed", ["Tags"] = new[] { "a", "b" }, ["Period.From"] = "2026-01-31" }),
            CancellationToken.None);

        result.IsError.Should().NotBe(true, Text(result));
        var body = result.StructuredContent!.Value;
        body.GetProperty("customerId").GetInt32().Should().Be(42);
        body.GetProperty("status").GetString().Should().Be("Closed");
        body.GetProperty("tags").EnumerateArray().Select(x => x.GetString()).Should().Equal("a", "b");
        body.GetProperty("from").GetString().Should().Be("2026-01-31");
        body.GetProperty("valid").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task CustomBinderRawFields_AreSentAsFormFields()
    {
        var caller = HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewLoans));
        var service = _host.CreateService(HostEndpointTestHost.OuterRequest(caller));

        var result = await service.CallAsync("api_loan_page", Args(new { request = new { page = 2, pageSize = 50, sort = "Name-asc" } }), CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.GetProperty("page").GetInt32().Should().Be(2);
        body.GetProperty("pageSize").GetInt32().Should().Be(50);
        body.GetProperty("sort").GetString().Should().Be("Name-asc");
    }

    [Test]
    public async Task MinimalApi_RouteAndQuery_AreBound_AndTheRouteValueIsEncoded()
    {
        var service = ThingsService();

        var result = await service.CallAsync("api_minimal_echo", Args(new { name = "a b/c", count = 2 }), CancellationToken.None);

        result.IsError.Should().NotBe(true, Text(result));
        result.StructuredContent!.Value.GetProperty("name").GetString().Should().Be("a b/c");
        result.StructuredContent.Value.GetProperty("count").GetInt32().Should().Be(2);
    }

    [Test]
    public async Task MinimalApi_FormWithAntiforgeryMetadata_IsBound()
    {
        var service = ThingsService();

        var result = await service.CallAsync("api_minimal_form", Args(new { term = "loans", limit = 3 }), CancellationToken.None);

        result.IsError.Should().NotBe(true, Text(result));
        result.StructuredContent!.Value.GetProperty("term").GetString().Should().Be("loans");
        result.StructuredContent.Value.GetProperty("limit").GetInt32().Should().Be(3);
    }

    [TestCase("/minimal/public-form", "api_minimal_public_form")]
    [TestCase("/admin/loans/public", "api_loan_public")]
    public async Task Antiforgery_StillRejectsRealRequestsWithoutAToken_WhileTheToolPasses(string path, string tool)
    {
        using var client = _host.App.GetTestClient();
        var real = await client.PostAsync(path, new FormUrlEncodedContent([new KeyValuePair<string, string>("term", "x"), new KeyValuePair<string, string>("q", "x")]));

        var result = await ThingsService().CallAsync(tool, tool == "api_loan_public" ? Args(new { q = "x" }) : Args(new { term = "x" }), CancellationToken.None);

        ((int)real.StatusCode).Should().Be(400, "a browser request without an antiforgery token is still rejected");
        result.IsError.Should().NotBe(true, Text(result));
    }

    [Test]
    public async Task TextResponse_IsReturnedAsText()
    {
        var result = await ThingsService().CallAsync("api_thing_text", null, CancellationToken.None);

        result.IsError.Should().NotBe(true);
        Text(result).Should().Be("plain words");
        result.StructuredContent.Should().BeNull();
    }

    [Test]
    public async Task NonTextResponse_IsAnError()
    {
        var result = await ThingsService().CallAsync("api_thing_file", null, CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().Contain("application/octet-stream").And.Contain("only JSON and text responses");
    }

    [Test]
    public async Task Non2xx_IsAnError_WithTheStatusAndABodyExcerpt()
    {
        var result = await ThingsService().CallAsync("api_thing_missing", null, CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().StartWith("The host endpoint returned HTTP 404 Not Found.").And.Contain("no such thing");
        _host.AuditLogs.Should().ContainSingle().Which.ErrorMessage.Should().Be("HTTP 404 Not Found");
    }

    [Test]
    public async Task AnEndpointException_IsAGenericError_WithoutItsMessage()
    {
        var result = await ThingsService().CallAsync("api_thing_throws", null, CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().NotContain("secret row data");
    }

    [Test]
    public async Task UnknownAndMissingArguments_AreRejectedBeforeDispatch()
    {
        var service = ThingsService();

        var unknown = await service.CallAsync("api_thing_by_id", Args(new { id = 1, bogus = true }), CancellationToken.None);
        var missing = await service.CallAsync("api_thing_by_id", Args(new { include = "x" }), CancellationToken.None);

        Text(unknown).Should().Contain("unknown argument(s) bogus");
        Text(missing).Should().Contain("missing required argument(s) id");
        _host.AuditLogs.Should().HaveCount(2).And.OnlyContain(x => x.ErrorMessage!.StartsWith("Invalid arguments"));
    }

    [Test]
    public async Task EveryCall_IsAudited_WithTheToolProjectAndCaller()
    {
        var service = ThingsService();

        await service.CallAsync("api_thing_by_id", Args(new { id = 3, include = "owner" }), CancellationToken.None);
        await _host.CreateService(HostEndpointTestHost.OuterRequest(HostEndpointTestHost.SystemCaller()))
            .CallAsync("api_thing_by_id", Args(new { id = 3 }), CancellationToken.None);

        _host.AuditLogs.Should().HaveCount(2);
        var success = _host.AuditLogs[0];
        success.Tool.Should().Be("api_thing_by_id");
        success.ProjectId.Should().Be(HostEndpointTestHost.ProjectId);
        success.UserId.Should().Be(11);
        success.CallerKind.Should().Be("System");
        success.CallerHash.Should().Be(HostEndpointTestHost.CallerHash);
        success.ErrorMessage.Should().BeNull();
        success.Parameters.Should().Contain("owner", "content is retained when the project allows it");

        _host.AuditLogs[1].ErrorMessage.Should().StartWith("Forbidden");
    }

    [Test]
    public async Task UnderTheRetentionLock_AuditKeepsOnlyStructure()
    {
        await using var host = await HostEndpointTestHost.StartAsync(retainContent: false);
        var service = host.CreateService(HostEndpointTestHost.OuterRequest(HostEndpointTestHost.SystemCaller()));

        await service.CallAsync("api_thing_by_id", Args(new { id = 3, include = "kennitala-123" }), CancellationToken.None);

        var row = host.AuditLogs.Should().ContainSingle().Subject;
        row.Parameters.Should().NotContain("kennitala-123");
        McpContentRedactor.IsStructuralAuditParameters(row.Parameters).Should().BeTrue();
        row.ErrorMessage.Should().Be(McpContentRedactor.ClassPermission);
    }

    [Test]
    public async Task AResponseOverTheCap_IsAnError()
    {
        await using var host = await HostEndpointTestHost.StartAsync(configure: x => x.MaxResponseBytes = 1024);
        var caller = HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings));

        var result = await host.CreateService(HostEndpointTestHost.OuterRequest(caller)).CallAsync("api_thing_big", null, CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().Contain("exceeded the 1024 byte limit");
    }

    [Test]
    public async Task ASlowEndpoint_TimesOut()
    {
        await using var host = await HostEndpointTestHost.StartAsync(configure: x => x.RequestTimeout = TimeSpan.FromMilliseconds(300));
        var caller = HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings));
        var sw = Stopwatch.StartNew();

        var result = await host.CreateService(HostEndpointTestHost.OuterRequest(caller)).CallAsync("api_thing_slow", null, CancellationToken.None);

        sw.Stop();
        result.IsError.Should().BeTrue();
        Text(result).Should().Contain("timed out");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2.5));
        host.AuditLogs.Should().ContainSingle().Which.ErrorMessage.Should().Contain("timed out");
    }

    [Test]
    public async Task NamedMode_ListsEachToolWithReadOnlyAnnotations()
    {
        var tools = await ThingsService().ListToolsAsync(CancellationToken.None);

        tools.Select(x => x.Name).Should().Contain("api_thing_by_id").And.NotContain(x => x.Contains("unmarked"));
        tools.Should().OnlyContain(x => x.Annotations!.ReadOnlyHint == true && x.Annotations.DestructiveHint == false);
        ThingsService().Handles("api_thing_by_id").Should().BeTrue();
        ThingsService().Handles("search_api").Should().BeFalse();
    }

    [Test]
    public async Task CatalogMode_ExposesSearchAndCall()
    {
        await using var host = await HostEndpointTestHost.StartAsync(configure: x => x.NamedToolLimit = 1);
        var caller = HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings));
        var service = host.CreateService(HostEndpointTestHost.OuterRequest(caller));

        var tools = await service.ListToolsAsync(CancellationToken.None);
        var search = await service.CallAsync("search_api", Args(new { query = "loan grid" }), CancellationToken.None);
        var call = await service.CallAsync("call_api", Args(new { name = "thing_by_id", arguments = new { id = 3 } }), CancellationToken.None);

        tools.Select(x => x.Name).Should().Equal("search_api", "call_api");
        service.Handles("api_thing_by_id").Should().BeFalse();
        var found = search.StructuredContent!.Value.GetProperty("tools").EnumerateArray().ToList();
        found[0].GetProperty("name").GetString().Should().Be("loan_grid");
        found[0].GetProperty("input_schema").GetProperty("properties").TryGetProperty("CustomerId", out _).Should().BeTrue();
        call.StructuredContent!.Value.GetProperty("id").GetInt32().Should().Be(3);
        host.AuditLogs.Select(x => x.Tool).Should().Equal("search_api", "api_thing_by_id");
    }

    [Test]
    public async Task TheCatalog_FeedsTheProjectBrief_ForItsProjectOnly()
    {
        var catalog = _host.App.Services.GetService(typeof(IHostEndpointToolCatalog)) as IHostEndpointToolCatalog;

        var listing = await catalog!.GetForProjectAsync(HostEndpointTestHost.ProjectId, CancellationToken.None);
        var other = await catalog.GetForProjectAsync(8, CancellationToken.None);

        other.Should().BeNull();
        listing!.CatalogMode.Should().BeFalse();
        listing.Tools.Should().Contain(new HostEndpointToolSummary("thing_by_id", "api_thing_by_id", "One thing, echoing what the endpoint saw."));
    }

    [TestCase(false, "- `api_thing_by_id` — One thing", "Call them by name.")]
    [TestCase(true, "- `thing_by_id` — One thing", "`search_api`")]
    public void TheBrief_ListsTheHostTools(bool catalogMode, string expectedLine, string expectedGuidance)
    {
        var data = new ProjectBriefData(
            "Netgiro", null, [], [], 0, [], [], 0, [], 0,
            new HostEndpointToolListing([new HostEndpointToolSummary("thing_by_id", "api_thing_by_id", "One thing.")], catalogMode));

        var brief = ProjectBriefService.Render(data);

        brief.Should().Contain("## Host application tools").And.Contain(expectedLine).And.Contain(expectedGuidance);
    }

    private HostEndpointToolService ThingsService()
    {
        var caller = HostEndpointTestHost.SystemCaller(
            HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings),
            HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewLoans));

        return _host.CreateService(HostEndpointTestHost.OuterRequest(caller));
    }

    internal static IDictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.SerializeToElement(value)
            .EnumerateObject()
            .ToDictionary(x => x.Name, x => x.Value.Clone());

    internal static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(x => x.Text));
}
