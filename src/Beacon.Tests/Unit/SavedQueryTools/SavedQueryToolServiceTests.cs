using System.Text.Json;
using Beacon.Core;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.SavedQueries;
using Beacon.MCP.SavedQueries;
using Beacon.MCP.Services;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.HostDocs;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Moq;
using NUnit.Framework;
using static Beacon.Tests.Unit.SavedQueryTools.SavedQueryTestData;

namespace Beacon.Tests.Unit.SavedQueryTools;

[TestFixture]
public class SavedQueryToolServiceTests
{
    private DocsStore _auditStore = null!;
    private Mock<ISavedQueryToolSource> _source = null!;
    private Mock<ISavedQueryToolExecutor> _executor = null!;
    private List<SavedQueryToolDefinition> _tools = null!;

    [SetUp]
    public void SetUp()
    {
        _auditStore = new DocsStore();
        _tools = [];
        _source = new Mock<ISavedQueryToolSource>();
        _source
            .Setup(x => x.GetToolsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<int> projects, CancellationToken _) =>
                _tools
                    .Where(x => x.ProjectIds.Any(projects.Contains))
                    .Select(x => x with { ProjectIds = x.ProjectIds.Where(projects.Contains).ToList() })
                    .ToList());
        _executor = new Mock<ISavedQueryToolExecutor>();
        _executor
            .Setup(x => x.ExecuteAsync(It.IsAny<SavedQueryToolDefinition>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SavedQueryToolExecution(true, null, [new Dictionary<string, object?> { ["month"] = "2026-01", ["total"] = 42m }], false, 1000, ["loans"], 10));
    }

    private List<McpAuditLog> Audits => _auditStore.ListFor<McpAuditLog>();

    [Test]
    public async Task UpToTheLimit_EachToolIsListedByName()
    {
        _tools.Add(LoanBook([1]));
        _tools.Add(Tool("arrears", [Step(1, 10, "SELECT 1")], [1], queryId: 2));

        var tools = await Service([1]).ListToolsAsync(CancellationToken.None);

        tools.Select(x => x.Name).Should().Equal("q_loan_book", "q_arrears");
        tools[0].Annotations!.ReadOnlyHint.Should().BeTrue();
        tools[0].Annotations!.OpenWorldHint.Should().BeFalse();
        tools[0].InputSchema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).Should().Equal("from", "to");
    }

    [Test]
    public async Task PastTheLimit_TheCatalogToolsAreListedInstead()
    {
        _tools.Add(LoanBook([1]));
        _tools.Add(Tool("arrears", [Step(1, 10, "SELECT 1")], [1], queryId: 2));

        var tools = await Service([1], namedToolLimit: 1).ListToolsAsync(CancellationToken.None);

        tools.Select(x => x.Name).Should().Equal(SavedQueryToolService.SearchToolName, SavedQueryToolService.RunToolName);
    }

    [Test]
    public async Task ACallerWithoutProjects_SeesNothing_AndTheSourceIsNotAsked()
    {
        _tools.Add(LoanBook([1]));

        var tools = await Service([]).ListToolsAsync(CancellationToken.None);

        tools.Should().BeEmpty();
        _source.Verify(x => x.GetToolsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task TheSource_IsAskedForTheCallersProjectsOnly()
    {
        _tools.Add(LoanBook([1, 2]));

        await Service([2]).ListToolsAsync(CancellationToken.None);

        _source.Verify(x => x.GetToolsAsync(It.Is<IReadOnlyCollection<int>>(y => y.SequenceEqual(new[] { 2 })), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ACall_RunsTheToolWithConvertedArguments_AndAuditsIt()
    {
        _tools.Add(LoanBook([1]));

        var result = await Service([1]).CallAsync("q_loan_book", Json("""{ "from": "2026-01-01", "to": "2026-02-01" }"""), CancellationToken.None);

        result.IsError.Should().NotBe(true, Text(result));
        Text(result).Should().Contain("q_loan_book").And.Contain("approved version 3").And.Contain("42");
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("rowCount").GetInt32().Should().Be(1);
        structured.GetProperty("truncated").GetBoolean().Should().BeFalse();
        structured.GetProperty("queryVersionId").GetInt32().Should().Be(100);
        structured.GetProperty("columns")[1].GetProperty("name").GetString().Should().Be("total");
        structured.GetProperty("columns")[1].GetProperty("type").GetString().Should().Be("number");
        structured.GetProperty("rows")[0].GetProperty("total").GetDecimal().Should().Be(42m);

        _executor.Verify(x => x.ExecuteAsync(
            It.Is<SavedQueryToolDefinition>(y => y.McpToolName == "loan_book"),
            1,
            It.Is<IReadOnlyDictionary<string, object?>>(y => (DateTime)y["from"]! == new DateTime(2026, 1, 1)),
            It.IsAny<CancellationToken>()), Times.Once);

        var audit = Audits.Should().ContainSingle().Subject;
        audit.Tool.Should().Be("q_loan_book");
        audit.ProjectId.Should().Be(1);
        audit.DataSourceId.Should().Be(10);
        audit.ResultRowCount.Should().Be(1);
        audit.ErrorMessage.Should().BeNull();
    }

    [Test]
    public async Task InvalidArguments_AreAToolError_AndAudited()
    {
        _tools.Add(LoanBook([1]));

        var result = await Service([1]).CallAsync("q_loan_book", Json("""{ "from": "2026-01-01" }"""), CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().Contain("Missing required argument 'to'");
        Audits.Should().ContainSingle(x => x.Tool == "q_loan_book" && x.ErrorMessage != null);
        _executor.Verify(x => x.ExecuteAsync(It.IsAny<SavedQueryToolDefinition>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AToolOutsideTheCallersProjects_IsUnknown_AndAudited()
    {
        _tools.Add(LoanBook([2]));

        var result = await Service([1]).CallAsync("q_loan_book", Json("""{ "from": "2026-01-01", "to": "2026-02-01" }"""), CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().Contain("Unknown saved-query tool");
        Audits.Should().ContainSingle(x => x.Tool == "q_loan_book" && x.ErrorMessage != null);
        _executor.Verify(x => x.ExecuteAsync(It.IsAny<SavedQueryToolDefinition>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AToolInSeveralProjects_NeedsProjectId_AndRejectsAnotherProject()
    {
        _tools.Add(LoanBook([1, 2]));
        var service = Service([1, 2]);

        var ambiguous = await service.CallAsync("q_loan_book", Json("""{ "from": "2026-01-01", "to": "2026-02-01" }"""), CancellationToken.None);
        var foreign = await service.CallAsync("q_loan_book", Json("""{ "from": "2026-01-01", "to": "2026-02-01", "project_id": 3 }"""), CancellationToken.None);
        var picked = await service.CallAsync("q_loan_book", Json("""{ "from": "2026-01-01", "to": "2026-02-01", "project_id": 2 }"""), CancellationToken.None);

        Text(ambiguous).Should().Contain("Pass project_id");
        Text(foreign).Should().Contain("Access denied");
        picked.IsError.Should().NotBe(true);
        _executor.Verify(x => x.ExecuteAsync(It.IsAny<SavedQueryToolDefinition>(), 2, It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Once);
        _executor.Verify(x => x.ExecuteAsync(It.IsAny<SavedQueryToolDefinition>(), 3, It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Never);
        Audits.Should().HaveCount(3);
    }

    [Test]
    public async Task AFailedExecution_IsAToolError_AndAuditedWithTheError()
    {
        _tools.Add(LoanBook([1]));
        _executor
            .Setup(x => x.ExecuteAsync(It.IsAny<SavedQueryToolDefinition>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SavedQueryToolExecution.Failed("Step 1 failed validation: write statement", ["loans"], 10));

        var result = await Service([1]).CallAsync("q_loan_book", Json("""{ "from": "2026-01-01", "to": "2026-02-01" }"""), CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().Contain("Step 1 failed validation");
        Audits.Should().ContainSingle().Which.ErrorMessage.Should().Contain("Step 1 failed validation");
    }

    [Test]
    public async Task AnExecutorException_IsAuditedAndSurfacedSafely()
    {
        _tools.Add(LoanBook([1]));
        _executor
            .Setup(x => x.ExecuteAsync(It.IsAny<SavedQueryToolDefinition>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NullReferenceException("internal detail"));

        var result = await Service([1]).CallAsync("q_loan_book", Json("""{ "from": "2026-01-01", "to": "2026-02-01" }"""), CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().NotContain("internal detail");
        Audits.Should().ContainSingle().Which.ErrorMessage.Should().Be("internal detail");
    }

    [Test]
    public async Task InCatalogMode_SearchFindsAToolAndRunSavedQueryRunsIt_AuditedUnderItsToolName()
    {
        _tools.Add(LoanBook([1]));
        _tools.Add(Tool("arrears", [Step(1, 10, "SELECT 1")], [1], queryId: 2));
        var service = Service([1], namedToolLimit: 1);

        var search = await service.CallAsync(SavedQueryToolService.SearchToolName, Json("""{ "query": "loan" }"""), CancellationToken.None);
        var run = await service.CallAsync(SavedQueryToolService.RunToolName, Json("""{ "name": "loan_book", "arguments": { "from": "2026-01-01", "to": "2026-02-01" } }"""), CancellationToken.None);

        var found = search.StructuredContent!.Value.GetProperty("tools");
        found.GetArrayLength().Should().Be(1);
        found[0].GetProperty("name").GetString().Should().Be("loan_book");
        found[0].GetProperty("input_schema").GetProperty("required").GetArrayLength().Should().Be(2);
        run.IsError.Should().NotBe(true, Text(run));
        Audits.Select(x => x.Tool).Should().Equal(SavedQueryToolService.SearchToolName, "q_loan_book");
    }

    [Test]
    public async Task RunSavedQuery_WithAnUnknownName_IsAuditedAsRunSavedQuery()
    {
        _tools.Add(LoanBook([1]));

        var result = await Service([1], namedToolLimit: 0).CallAsync(SavedQueryToolService.RunToolName, Json("""{ "name": "nope" }"""), CancellationToken.None);

        result.IsError.Should().BeTrue();
        Audits.Should().ContainSingle(x => x.Tool == SavedQueryToolService.RunToolName);
    }

    [TestCase("q_anything", true)]
    [TestCase("search_saved_queries", true)]
    [TestCase("run_saved_query", true)]
    [TestCase("query", false)]
    [TestCase("api_thing", false)]
    [TestCase(null, false)]
    public void Handles_OnlyItsOwnNames(string? name, bool expected)
    {
        SavedQueryToolService.Handles(name).Should().Be(expected);
    }

    private static SavedQueryToolDefinition LoanBook(IReadOnlyList<int> projects) =>
        Tool("loan_book", [Step(1, 10, "SELECT * FROM loans WHERE issued >= {from} AND issued < {to}", Parameter("from", ParameterType.DateTime), Parameter("to", ParameterType.DateTime))], projects);

    private SavedQueryToolService Service(List<int> allowedProjects, int namedToolLimit = 25)
    {
        var projectContext = new McpProjectContext { AllowedProjectIds = allowedProjects, UserId = 11 };
        var audit = new McpAuditService(
            _auditStore.Factory().Object,
            SettingsProviderMock.Create(new McpSettingsData { RetainQueryContent = true }).Object,
            new HttpContextAccessor(),
            NullLogger<McpAuditService>.Instance);
        var configuration = new BeaconConfiguration { SavedQueryTools = new SavedQueryToolOptions { NamedToolLimit = namedToolLimit } };

        return new SavedQueryToolService(_source.Object, _executor.Object, projectContext, audit, NullLogger<SavedQueryToolService>.Instance, configuration);
    }

    private static Dictionary<string, JsonElement> Json(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone());

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(x => x.Text));
}
