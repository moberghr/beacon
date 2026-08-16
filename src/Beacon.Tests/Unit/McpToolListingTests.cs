using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using NUnit.Framework;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.MCP.Services;

namespace Beacon.Tests.Unit;

/// <summary>
/// Verifies the tools/list description-override rewrite (<see cref="McpToolDescriptionOverrides"/>):
/// an admin-configured, non-empty override from <see cref="McpSettingsData"/> replaces the compiled
/// [Description] text, while unset / whitespace overrides and unmapped tools keep the original.
/// Also exercises the request filter from <see cref="McpToolDescriptionOverrides.CreateListToolsFilter"/>
/// end to end: overrides are cosmetic, so a missing settings provider or a failing settings read must
/// never break tools/list — only cancellation propagates.
/// </summary>
[TestFixture]
public class McpToolListingTests
{
    private const string CompiledDescription = "Compiled attribute description.";

    [Test]
    public void Apply_OverrideSet_ReplacesDescription()
    {
        var tools = AllTools();
        var settings = new McpSettingsData { GetContextDescription = "Admin override for get_context." };

        McpToolDescriptionOverrides.Apply(tools, settings);

        tools.Single(x => x.Name == "get_context").Description.Should().Be("Admin override for get_context.");
    }

    [Test]
    public void Apply_AllFiveOverridesSet_EachToolGetsItsOwnOverride()
    {
        var tools = AllTools();
        var settings = new McpSettingsData
        {
            GetContextDescription = "ctx",
            SearchDescription = "search",
            GetDocumentationDescription = "docs",
            QueryDescription = "query",
            AskDescription = "ask"
        };

        McpToolDescriptionOverrides.Apply(tools, settings);

        tools.Single(x => x.Name == "get_context").Description.Should().Be("ctx");
        tools.Single(x => x.Name == "search").Description.Should().Be("search");
        tools.Single(x => x.Name == "get_documentation").Description.Should().Be("docs");
        tools.Single(x => x.Name == "query").Description.Should().Be("query");
        tools.Single(x => x.Name == "ask").Description.Should().Be("ask");
    }

    [Test]
    public void Apply_NullOverride_KeepsCompiledDescription()
    {
        var tools = AllTools();

        McpToolDescriptionOverrides.Apply(tools, new McpSettingsData());

        tools.Should().OnlyContain(x => x.Description == CompiledDescription);
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Apply_EmptyOrWhitespaceOverride_KeepsCompiledDescription(string overrideValue)
    {
        var tools = AllTools();
        var settings = new McpSettingsData
        {
            GetContextDescription = overrideValue,
            SearchDescription = overrideValue,
            GetDocumentationDescription = overrideValue,
            QueryDescription = overrideValue,
            AskDescription = overrideValue
        };

        McpToolDescriptionOverrides.Apply(tools, settings);

        tools.Should().OnlyContain(x => x.Description == CompiledDescription);
    }

    [Test]
    public void Apply_ToolWithoutOverrideField_KeepsCompiledDescription()
    {
        // feedback has no admin override column — it must never be rewritten, even
        // when every configurable override is set.
        var tools = AllTools();
        var settings = new McpSettingsData
        {
            GetContextDescription = "ctx",
            SearchDescription = "search",
            GetDocumentationDescription = "docs",
            QueryDescription = "query",
            AskDescription = "ask"
        };

        McpToolDescriptionOverrides.Apply(tools, settings);

        tools.Single(x => x.Name == "feedback").Description.Should().Be(CompiledDescription);
    }

    [Test]
    public void Apply_NullToolList_DoesNotThrow()
    {
        var act = () => McpToolDescriptionOverrides.Apply(null, new McpSettingsData());

        act.Should().NotThrow();
    }

    [Test]
    public void Apply_OverrideSetThenCleared_NeverMutatesSingletonToolInstances()
    {
        // The SDK exposes Tool instances as process-wide singletons (AIFunctionMcpServerTool.ProtocolTool);
        // every tools/list response wraps the SAME instances in a fresh list. Mutating them would leak an
        // override into every later response even after the admin clears it.
        var singletons = AllTools();
        var original = singletons.Single(x => x.Name == "get_context");

        var firstResponse = new List<Tool>(singletons);
        McpToolDescriptionOverrides.Apply(firstResponse, new McpSettingsData { GetContextDescription = "Admin override for get_context." });

        var replaced = firstResponse.Single(x => x.Name == "get_context");
        replaced.Description.Should().Be("Admin override for get_context.");
        replaced.Should().NotBeSameAs(original);
        original.Description.Should().Be(CompiledDescription);

        var secondResponse = new List<Tool>(singletons);
        McpToolDescriptionOverrides.Apply(secondResponse, new McpSettingsData());

        secondResponse.Single(x => x.Name == "get_context").Description.Should().Be(CompiledDescription);
        secondResponse.Single(x => x.Name == "get_context").Should().BeSameAs(original);
    }

    [Test]
    public void Apply_OverrideSet_CloneCarriesAllWireProperties()
    {
        var annotations = new ToolAnnotations { ReadOnlyHint = true };
        var original = new Tool
        {
            Name = "search",
            Title = "Search Catalog",
            Description = CompiledDescription,
            Annotations = annotations
        };
        var tools = new List<Tool> { original };

        McpToolDescriptionOverrides.Apply(tools, new McpSettingsData { SearchDescription = "override" });

        var clone = tools.Single();
        clone.Should().NotBeSameAs(original);
        clone.Name.Should().Be("search");
        clone.Title.Should().Be("Search Catalog");
        clone.Description.Should().Be("override");
        clone.InputSchema.GetRawText().Should().Be(original.InputSchema.GetRawText());
        clone.OutputSchema.Should().Be(original.OutputSchema);
        clone.Annotations.Should().BeSameAs(annotations);
        clone.Icons.Should().BeSameAs(original.Icons);
        clone.Meta.Should().BeSameAs(original.Meta);
    }

    [Test]
    public async Task ListToolsFilter_SettingsProviderThrows_ServesCompiledDescriptionsUnmodified()
    {
        var settingsProvider = new Mock<IMcpSettingsProvider>();
        settingsProvider.Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("settings read failed"));
        var services = new ServiceCollection()
            .AddSingleton(settingsProvider.Object)
            .BuildServiceProvider();

        // No exception may escape — completing normally with the compiled descriptions IS the contract.
        var result = await InvokeListToolsFilterAsync(services);

        result.Tools.Should().OnlyContain(x => x.Description == CompiledDescription);
    }

    [Test]
    public async Task ListToolsFilter_NoSettingsProviderRegistered_ServesCompiledDescriptionsUnmodified()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        var result = await InvokeListToolsFilterAsync(services);

        result.Tools.Should().OnlyContain(x => x.Description == CompiledDescription);
    }

    [Test]
    public async Task ListToolsFilter_ProviderReturnsOverride_RewritesListedDescription()
    {
        var settingsProvider = new Mock<IMcpSettingsProvider>();
        settingsProvider.Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData { GetContextDescription = "Admin override for get_context." });
        var services = new ServiceCollection()
            .AddSingleton(settingsProvider.Object)
            .BuildServiceProvider();

        var result = await InvokeListToolsFilterAsync(services);

        result.Tools.Single(x => x.Name == "get_context").Description.Should().Be("Admin override for get_context.");
        result.Tools.Where(x => x.Name != "get_context").Should().OnlyContain(x => x.Description == CompiledDescription);
    }

    [Test]
    public async Task ListToolsFilter_OperationCanceled_Propagates()
    {
        var settingsProvider = new Mock<IMcpSettingsProvider>();
        settingsProvider.Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var services = new ServiceCollection()
            .AddSingleton(settingsProvider.Object)
            .BuildServiceProvider();

        var act = () => InvokeListToolsFilterAsync(services);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation is not a cosmetic-override failure and must propagate");
    }

    private static async Task<ListToolsResult> InvokeListToolsFilterAsync(IServiceProvider? services)
    {
        var handler = McpToolDescriptionOverrides.CreateListToolsFilter()(
            (x, y) => ValueTask.FromResult(new ListToolsResult { Tools = AllTools() }));
        var request = new RequestContext<ListToolsRequestParams>(
            Mock.Of<McpServer>(), new JsonRpcRequest { Method = "tools/list" })
        {
            Services = services
        };

        return await handler(request, CancellationToken.None);
    }

    private static List<Tool> AllTools() =>
    [
        new Tool { Name = "get_context", Description = CompiledDescription },
        new Tool { Name = "search", Description = CompiledDescription },
        new Tool { Name = "get_documentation", Description = CompiledDescription },
        new Tool { Name = "query", Description = CompiledDescription },
        new Tool { Name = "ask", Description = CompiledDescription },
        new Tool { Name = "feedback", Description = CompiledDescription }
    ];
}
