using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Beacon.Core.HostData;
using Beacon.MCP.HostEndpoints;

namespace Beacon.Tests.Unit.HostEndpoints;

[TestFixture]
public class HostEndpointDiscoveryTests
{
    private HostEndpointTestHost _host = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await HostEndpointTestHost.StartAsync(services: x => x.AddSingleton<IXmlDocumentationProvider>(new FakeMethodDocs()));
    }

    [OneTimeTearDown]
    public async Task StopHost()
    {
        await _host.DisposeAsync();
    }

    [Test]
    public void Discovery_ExposesOnlyMarkedEndpoints_SortedByName()
    {
        _host.Registry.Tools.Select(x => x.Name).Should().Equal(
            "loan_grid",
            "loan_page",
            "loan_public",
            "minimal_echo",
            "minimal_form",
            "minimal_public_form",
            "thing_big",
            "thing_by_id",
            "thing_file",
            "thing_missing",
            "thing_search",
            "thing_slow",
            "thing_text",
            "thing_throws");

        _host.Registry.Tools.Should().OnlyContain(x => x.ToolName == "api_" + x.Name);
    }

    [Test]
    public void Schema_RouteQueryAndHeader_FromApiExplorer()
    {
        var tool = _host.Tool("thing_by_id");

        tool.HttpMethod.Should().Be("GET");
        tool.Parameters.Select(x => (x.ArgumentName, x.Source)).Should().BeEquivalentTo(new[]
        {
            ("id", HostEndpointParameterSource.Route),
            ("include", HostEndpointParameterSource.Query),
            ("X-Tenant", HostEndpointParameterSource.Header)
        });

        var schema = tool.InputSchema;
        schema.GetProperty("properties").GetProperty("id").GetProperty("type").GetString().Should().Be("integer");
        schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).Should().Equal("id");
        schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        schema.GetProperty("properties").GetProperty("include").GetProperty("description").GetString()
            .Should().Be("What to include (from XML docs).");
    }

    [Test]
    public void Schema_JsonBody_UsesTheExportedTypeSchema()
    {
        var tool = _host.Tool("thing_search");

        tool.HttpMethod.Should().Be("POST");
        var request = tool.InputSchema.GetProperty("properties").GetProperty("request");
        var properties = request.GetProperty("properties");
        properties.GetProperty("nameContains").GetProperty("description").GetString().Should().Be("Case-insensitive part of the name.");
        properties.GetProperty("take").GetProperty("type").GetString().Should().Be("integer");
        tool.Parameters.Single().Shape.Should().Be(HostEndpointParameterShape.Json);
    }

    [Test]
    public void Schema_MvcFormModel_IsFlattenedIntoFields()
    {
        var tool = _host.Tool("loan_grid");
        var properties = tool.InputSchema.GetProperty("properties");

        tool.Parameters.Should().OnlyContain(x => x.Source == HostEndpointParameterSource.Form);
        properties.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo("CustomerId", "Status", "Tags", "Period.From", "Period.To");
        properties.GetProperty("Status").GetProperty("enum").EnumerateArray().Select(x => x.GetString()).Should().Equal("Active", "Closed");
        properties.GetProperty("Tags").GetProperty("type").ToString().Should().Contain("array");
        tool.InputSchema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).Should().Equal("CustomerId");
        tool.Parameters.Single(x => x.ArgumentName == "Tags").Shape.Should().Be(HostEndpointParameterShape.Collection);
    }

    [Test]
    public void Schema_CustomModelBinder_TakesRawFields()
    {
        var parameter = _host.Tool("loan_page").Parameters.Single();

        parameter.Shape.Should().Be(HostEndpointParameterShape.RawFields);
        parameter.Source.Should().Be(HostEndpointParameterSource.Form);
        parameter.Schema.GetProperty("type").GetString().Should().Be("object");
    }

    [Test]
    public void Schema_MinimalApi_InfersRouteQueryAndForm()
    {
        var echo = _host.Tool("minimal_echo");
        echo.Parameters.Select(x => (x.ArgumentName, x.Source, x.Required)).Should().BeEquivalentTo(new[]
        {
            ("name", HostEndpointParameterSource.Route, true),
            ("count", HostEndpointParameterSource.Query, false)
        });

        var form = _host.Tool("minimal_form");
        form.Parameters.Select(x => (x.ArgumentName, x.Source, x.Required)).Should().BeEquivalentTo(new[]
        {
            ("term", HostEndpointParameterSource.Form, true),
            ("limit", HostEndpointParameterSource.Form, false)
        });
    }

    [Test]
    public void Description_PrefersTheAttribute_ThenXmlDocs_ThenAGenericLine()
    {
        _host.Tool("thing_by_id").Description.Should().StartWith("One thing, echoing what the endpoint saw.");
        _host.Tool("loan_grid").Description.Should().StartWith("Loans of one customer (from XML docs).");
        _host.Tool("thing_big").Description.Should().StartWith("Calls GET /things/big on the host application.");
        _host.Tool("thing_big").Description.Should().Contain("Read-only host endpoint GET /things/big");
    }

    [Test]
    public void ProtocolTool_CarriesReadOnlyAnnotations()
    {
        var tool = _host.Tool("thing_search").ToProtocolTool();

        tool.Name.Should().Be("api_thing_search");
        tool.Annotations!.ReadOnlyHint.Should().BeTrue();
        tool.Annotations.DestructiveHint.Should().BeFalse();
        tool.Annotations.OpenWorldHint.Should().BeFalse();
    }

    [TestCase(typeof(MissingReadOnlyController), "ReadOnly must be declared true")]
    [TestCase(typeof(FalseReadOnlyController), "ReadOnly must be declared true")]
    [TestCase(typeof(BadNameController), "must match ^[a-z][a-z0-9_]{2,47}$")]
    [TestCase(typeof(DuplicateNameController), "tool name 'same_name' is already used by")]
    [TestCase(typeof(NoAuthorizationController), "declares no authorization")]
    [TestCase(typeof(StepUpSchemeController), "requires authentication scheme(s) 'StepUp'")]
    public async Task Startup_FailsForMisconfiguredMarkers(Type controller, string expected)
    {
        var start = () => HostEndpointTestHost.StartAsync([controller], mapDefaultMinimalApis: false);

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Beacon host endpoint tools are misconfigured").And.Contain(expected);
    }

    [Test]
    public async Task Startup_AcceptsAnUnannotatedEndpoint_WhenTheHostHasAFallbackPolicy()
    {
        await using var host = await HostEndpointTestHost.StartAsync([typeof(NoAuthorizationController)], mapDefaultMinimalApis: false, fallbackPolicy: true);

        host.Registry.Tools.Select(x => x.Name).Should().Equal("no_auth");
    }

    [Test]
    public async Task Startup_AcceptsASchemeBoundEndpoint_OnlyWhenTheHostTrustsTheScheme()
    {
        await using var host = await HostEndpointTestHost.StartAsync(
            [typeof(StepUpSchemeController)],
            mapDefaultMinimalApis: false,
            configure: x => x.TrustedAuthenticationSchemes = [StepUpSchemeController.Scheme]);

        host.Registry.Tools.Select(x => x.Name).Should().Equal("step_up");
    }

    [Test]
    public async Task Startup_FailsForAMinimalApiWhosePolicyNamesAScheme()
    {
        var policy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder("Partner")
            .RequireAuthenticatedUser()
            .Build();
        var start = () => HostEndpointTestHost.StartAsync(
            [],
            mapDefaultMinimalApis: false,
            map: x => x.MapGet("/minimal/partner", () => Results.Ok()).RequireAuthorization(policy).WithBeaconTool("minimal_partner", null, readOnly: true));

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("minimal_partner").And.Contain("'Partner'");
    }

    [Test]
    public async Task Startup_FailsForAMinimalApiThatIsNotDeclaredReadOnly()
    {
        var start = () => HostEndpointTestHost.StartAsync(
            [],
            mapDefaultMinimalApis: false,
            map: x => x.MapGet("/minimal/write", () => Results.Ok()).RequireAuthorization().WithBeaconTool("minimal_write", null, readOnly: false));

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("minimal_write").And.Contain("ReadOnly must be declared true");
    }

    [Test]
    public void AddHostEndpointTools_RejectsMissingProjectName_AndASecondCall()
    {
        var services = new ServiceCollection();

        var missing = () => services.AddHostEndpointTools(x => x.ProjectName = " ");
        missing.Should().Throw<InvalidOperationException>().WithMessage("*ProjectName is required*");

        services.AddHostEndpointTools(x => x.ProjectName = "Netgiro");
        var twice = () => services.AddHostEndpointTools(x => x.ProjectName = "Netgiro");
        twice.Should().Throw<InvalidOperationException>().WithMessage("*only be called once*");
    }

    [Test]
    public void XmlDocs_ParseMethodSummaryAndParams_AndResolveTheMemberId()
    {
        var method = typeof(ThingsController).GetMethod(nameof(ThingsController.Get))!;
        var memberId = AssemblyXmlDocumentationProvider.MethodMemberName(method);
        memberId.Should().Be("M:Beacon.Tests.Unit.HostEndpoints.ThingsController.Get(System.Int32,System.String,System.String)");

        var document = XDocument.Parse(
            $"""
            <doc><members>
              <member name="{memberId}">
                <summary>One <see cref="T:Beacon.Tests.Unit.HostEndpoints.ThingDto"/> by id.</summary>
                <param name="id">The thing id.</param>
                <param name="include">What to include.</param>
              </member>
              <member name="M:Beacon.Tests.Unit.HostEndpoints.AdminLoansController.Grid(Some.Other.Encoding)">
                <summary>Grid.</summary>
              </member>
            </members></doc>
            """);
        var members = AssemblyXmlDocumentationProvider.Parse(document);

        members[memberId].Should().Be("One ThingDto by id.");
        members[memberId + AssemblyXmlDocumentationProvider.ParameterSeparator + "include"].Should().Be("What to include.");
        AssemblyXmlDocumentationProvider.ResolveMethodMemberName(members, method).Should().Be(memberId);

        // The only documented overload is used when the exact id encoding differs.
        var grid = typeof(AdminLoansController).GetMethod(nameof(AdminLoansController.Grid))!;
        AssemblyXmlDocumentationProvider.ResolveMethodMemberName(members, grid)
            .Should().Be("M:Beacon.Tests.Unit.HostEndpoints.AdminLoansController.Grid(Some.Other.Encoding)");
    }

    private sealed class FakeMethodDocs : IXmlDocumentationProvider
    {
        public string? GetTypeSummary(Type type) => null;

        public string? GetPropertySummary(Type declaringType, string propertyName) => null;

        public XmlMethodDocumentation? GetMethodDocumentation(MethodInfo method)
        {
            if (method.DeclaringType == typeof(AdminLoansController) && method.Name == nameof(AdminLoansController.Grid))
            {
                return new XmlMethodDocumentation("Loans of one customer (from XML docs).", new Dictionary<string, string>());
            }

            if (method.DeclaringType == typeof(ThingsController) && method.Name == nameof(ThingsController.Get))
            {
                return new XmlMethodDocumentation("Ignored: the attribute wins.", new Dictionary<string, string> { ["include"] = "What to include (from XML docs)." });
            }

            return null;
        }
    }
}
