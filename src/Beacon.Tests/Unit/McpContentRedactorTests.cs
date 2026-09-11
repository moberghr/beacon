using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Data.Entities;
using Beacon.Core.Services.Retention;

namespace Beacon.Tests.Unit;

/// <summary>
/// Review finding F1 (Stage 2 test lane): the nine-class error vocabulary is a declared external contract — an
/// operator reads these classes instead of the error text once the content lock is on — but only two of the nine
/// were exercised, indirectly, by the interceptor tests. These pin the whole vocabulary, the precedence order that
/// resolves a message matching several classifiers, idempotency, and the null/empty edges.
/// </summary>
[TestFixture]
public class McpContentRedactorTests
{
    [TestCase("relation \"orders\" does not exist", McpContentRedactor.ClassSchema)]
    [TestCase("column \"revenu\" of relation \"orders\" does not exist", McpContentRedactor.ClassSchema)]
    [TestCase("syntax error at or near \"FROM\"", McpContentRedactor.ClassSyntax)]
    [TestCase("permission denied for table orders", McpContentRedactor.ClassPermission)]
    [TestCase("canceling statement due to statement timeout", McpContentRedactor.ClassTimeout)]
    [TestCase("no such table: customers", McpContentRedactor.ClassNotFound)]
    [TestCase("invalid input value for enum status", McpContentRedactor.ClassValidation)]
    [TestCase("deadlock detected while executing the batch", McpContentRedactor.ClassExecution)]
    [TestCase("query cancelled by the client", McpContentRedactor.ClassCancelled)]
    [TestCase("canceling statement due to user request", McpContentRedactor.ClassCancelled)]
    [TestCase("Invalid object name 'orders'.", McpContentRedactor.ClassNotFound)]
    [TestCase("Table 'beacon.orders' doesn't exist", McpContentRedactor.ClassNotFound)]
    [TestCase("Unknown column 'revenu' in 'field list'", McpContentRedactor.ClassSchema)]
    [TestCase("Unknown table 'beacon.orders'", McpContentRedactor.ClassNotFound)]
    public void ErrorClassOf_MapsProviderMessagesOntoTheVocabulary(string message, string expected)
    {
        McpContentRedactor.ErrorClassOf(message).Should().Be(expected);
    }

    [Test]
    public void ErrorClassOf_UnrecognisedMessage_FallsBackToUnknown()
    {
        McpContentRedactor.ErrorClassOf("the warehouse said something we have never seen before")
            .Should().Be(McpContentRedactor.ClassUnknown);
    }

    [TestCase("permission denied for relation orders", McpContentRedactor.ClassPermission,
        TestName = "permission wins over schema (the message also contains 'relation')")]
    [TestCase("canceling statement due to statement timeout", McpContentRedactor.ClassTimeout,
        TestName = "timeout wins over cancelled ('canceling' + 'timeout')")]
    [TestCase("invalid column name 'revenu'", McpContentRedactor.ClassSchema,
        TestName = "schema wins over validation ('invalid' + 'column')")]
    [TestCase("Invalid object name 'orders'.", McpContentRedactor.ClassNotFound,
        TestName = "SQL Server schema drift is not_found, not validation ('invalid' + 'invalid object name')")]
    public void ErrorClassOf_AmbiguousMessage_FollowsTheDeclaredPrecedence(string message, string expected)
    {
        // The classifier table is ordered on purpose: provider messages routinely match several vocabularies at
        // once. If someone reorders the table, these break rather than silently reclassifying audit history.
        McpContentRedactor.ErrorClassOf(message).Should().Be(expected);
    }

    [Test]
    public void ErrorClassOf_IsIdempotent_SoASecondPassNeverReclassifies()
    {
        // The belt may see a row the brace already classified (a Modified entry). Re-running must be a no-op,
        // otherwise "permission" would be re-read as its own keyword match on every subsequent save.
        foreach (var errorClass in new[]
                 {
                     McpContentRedactor.ClassSchema, McpContentRedactor.ClassSyntax, McpContentRedactor.ClassPermission,
                     McpContentRedactor.ClassTimeout, McpContentRedactor.ClassNotFound, McpContentRedactor.ClassValidation,
                     McpContentRedactor.ClassExecution, McpContentRedactor.ClassCancelled, McpContentRedactor.ClassUnknown
                 })
        {
            McpContentRedactor.ErrorClassOf(errorClass).Should().Be(errorClass);
        }
    }

    [Test]
    public void ErrorClassOf_NullStaysNull_AndEmptyStaysEmpty()
    {
        McpContentRedactor.ErrorClassOf(null).Should().BeNull("a row with no error must not gain one");
        McpContentRedactor.ErrorClassOf(string.Empty).Should().BeEmpty();
    }

    [Test]
    public void StructuralAuditParameters_CarriesTheByteCountAndTables_ButNoContent()
    {
        var sql = "SELECT sum(revenue) FROM orders WHERE customer = 'acme'";

        var structural = McpContentRedactor.StructuralAuditParameters("query", sql, ["orders"]);

        structural.Should().NotContain("acme").And.NotContain("revenue");
        using var document = JsonDocument.Parse(structural);
        document.RootElement.GetProperty("tool").GetString().Should().Be("query");
        document.RootElement.GetProperty("params")[0].GetProperty("bytes").GetInt32().Should().Be(sql.Length);
        document.RootElement.GetProperty("tables").EnumerateArray().Select(x => x.GetString()).Should().Equal("orders");
        McpContentRedactor.IsStructuralAuditParameters(structural).Should().BeTrue();
    }

    [Test]
    public void StructuralAuditParameters_NullParameters_ReportZeroBytes_AndNoTablesIsAnEmptyArray()
    {
        var structural = McpContentRedactor.StructuralAuditParameters("get_context", null, null);

        using var document = JsonDocument.Parse(structural);
        document.RootElement.GetProperty("params")[0].GetProperty("bytes").GetInt32().Should().Be(0);
        document.RootElement.GetProperty("tables").EnumerateArray().Should().BeEmpty();
    }

    [Test]
    public void RedactSignal_ClearsContent_ClassifiesErrors_AndLeavesStructureAlone()
    {
        var signal = new McpQuerySignal
        {
            Tool = "ask",
            Question = "how much revenue did acme make?",
            ProjectId = 42,
            DataSourceId = 7,
            RoutingDecision = "[{\"DataSourceId\":7,\"Reason\":\"acme lives in the warehouse\"}]",
            GeneratedSql = "SELECT sum(revenue) FROM orders",
            CorrectedSql = "SELECT sum(revenue) FROM orders o",
            UserCorrectedSql = "SELECT sum(o.revenue) FROM orders o",
            FeedbackNote = "the customer meant net revenue",
            TablesUsed = "[\"orders\"]",
            ColumnsUsed = "[\"revenue\"]",
            SchemaValidationError = "column \"revenu\" does not exist",
            ExecutionError = "permission denied for table orders",
            DryRunError = "syntax error at or near \"FORM\"",
            IsSuccessful = false,
            ExecutionTimeMs = 120,
            CallerHash = "abc123"
        };

        McpContentRedactor.RedactSignal(signal);

        signal.Question.Should().BeEmpty();
        signal.RoutingDecision.Should().BeNull("routing reasons quote the question");
        signal.GeneratedSql.Should().BeNull();
        signal.CorrectedSql.Should().BeNull();
        signal.UserCorrectedSql.Should().BeNull();
        signal.FeedbackNote.Should().BeNull();
        signal.SchemaValidationError.Should().Be(McpContentRedactor.ClassSchema);
        signal.ExecutionError.Should().Be(McpContentRedactor.ClassPermission);
        signal.DryRunError.Should().Be(McpContentRedactor.ClassSyntax);

        signal.Tool.Should().Be("ask");
        signal.ProjectId.Should().Be(42);
        signal.DataSourceId.Should().Be(7);
        signal.TablesUsed.Should().Be("[\"orders\"]");
        signal.ColumnsUsed.Should().Be("[\"revenue\"]");
        signal.ExecutionTimeMs.Should().Be(120);
        signal.CallerHash.Should().Be("abc123");
    }

    [Test]
    public void RedactEvalResult_ClearsSqlAndVerdict_AndClassifiesTheError()
    {
        var result = new McpEvalResult
        {
            EvalRunId = 1,
            EvalCaseId = 2,
            GeneratedSql = "SELECT sum(revenue) FROM orders",
            ExecutionError = "relation \"orders\" does not exist",
            JudgeVerdict = "the generated query counts acme twice",
            ResultRowCount = 3,
            ExecutionTimeMs = 40
        };

        McpContentRedactor.RedactEvalResult(result);

        result.GeneratedSql.Should().BeNull();
        result.JudgeVerdict.Should().BeNull("the judge quotes the rows it compared");
        result.ExecutionError.Should().Be(McpContentRedactor.ClassSchema);
        result.ResultRowCount.Should().Be(3, "counts are structure");
        result.ExecutionTimeMs.Should().Be(40);
    }
}
