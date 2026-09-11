using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Interceptors;
using Beacon.Core.Services.Retention;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC3 of spec <c>retention-lock</c>: the EF belt. Entities are tracked on <see cref="NpgsqlTestContext"/> (change
/// tracking only — no database, §4.7) and the interceptor's redaction core is driven with an injected decision:
/// under the lock content is nulled and free-text errors become their class, structural columns are untouched, and a
/// resolver failure fails closed with an identifier-only Warning (§1.11).
/// </summary>
[TestFixture]
public class ContentRetentionInterceptorTests
{
    private const int ProjectId = 42;

    private CapturingLogger _logger = null!;
    private ContentRetentionInterceptor _interceptor = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new CapturingLogger();
        _interceptor = new ContentRetentionInterceptor(Mock.Of<IServiceProvider>(), _logger);
    }

    [Test]
    public async Task LockedProject_ClearsContent_ClassifiesErrors_AndKeepsStructure()
    {
        using var context = NpgsqlTestContext.Create();
        var signal = NewSignal();
        var audit = NewAuditLog();
        var pattern = NewPattern();
        context.AddRange(signal, audit, pattern);
        var resolvedProjects = new List<int?>();

        await _interceptor.RedactTrackedEntriesAsync(
            context.ChangeTracker,
            (x, _) =>
            {
                resolvedProjects.Add(x);

                return Task.FromResult(new ContentRetentionDecision(false, false));
            });

        signal.Question.Should().BeEmpty();
        signal.GeneratedSql.Should().BeNull();
        signal.RoutingDecision.Should().BeNull();
        signal.UserCorrectedSql.Should().BeNull();
        signal.FeedbackNote.Should().BeNull();
        signal.ExecutionError.Should().Be(McpContentRedactor.ClassSchema);
        signal.Tool.Should().Be("ask");
        signal.TablesUsed.Should().Be("public.users");
        signal.IntentClassification.Should().Be("aggregation");
        audit.Parameters.Should().BeNull("free-text parameters are content");
        audit.ErrorMessage.Should().Be(McpContentRedactor.ClassPermission);
        audit.Tool.Should().Be("query");
        pattern.ExampleQuestion.Should().BeNull();
        pattern.ExampleSql.Should().BeNull();
        pattern.PatternContent.Should().Be("column public.users.email is an email address");
        pattern.TableName.Should().Be("users");
        resolvedProjects.Should().OnlyContain(x => x == ProjectId);
        resolvedProjects.Should().ContainSingle("one settings read covers every row of the same project");
        _logger.Warnings.Should().HaveCount(10, "one Warning per cleared property");
        _logger.Warnings.Should().OnlyContain(x => !x.Contains("how many users") && !x.Contains("select"));
    }

    [Test]
    public async Task UnlockedProject_ChangesNothing()
    {
        using var context = NpgsqlTestContext.Create();
        var signal = NewSignal();
        var audit = NewAuditLog();
        var pattern = NewPattern();
        context.AddRange(signal, audit, pattern);

        await _interceptor.RedactTrackedEntriesAsync(
            context.ChangeTracker,
            (_, _) => Task.FromResult(new ContentRetentionDecision(true, true)));

        signal.Question.Should().Be("how many users signed up?");
        signal.GeneratedSql.Should().Be("select count(*) from users");
        signal.ExecutionError.Should().Be("relation \"users\" does not exist");
        audit.Parameters.Should().Be("{\"sql\":\"select count(*) from users\"}");
        audit.ErrorMessage.Should().Be("permission denied for table users");
        pattern.ExampleQuestion.Should().Be("how many users signed up?");
        _logger.Warnings.Should().BeEmpty();
    }

    [Test]
    public async Task ResolverThrows_FailsClosed_AndLogsIdentifiersOnly()
    {
        using var context = NpgsqlTestContext.Create();
        var signal = NewSignal();
        signal.Id = 314;
        context.Add(signal);

        await _interceptor.RedactTrackedEntriesAsync(
            context.ChangeTracker,
            (_, _) => throw new InvalidOperationException("settings unavailable"));

        signal.Question.Should().BeEmpty();
        signal.GeneratedSql.Should().BeNull();
        signal.ExecutionError.Should().Be(McpContentRedactor.ClassSchema);
        _logger.Warnings.Should().Contain(x => x.Contains(nameof(McpQuerySignal)) && x.Contains("314"));
        _logger.Warnings.Should().OnlyContain(x => !x.Contains("how many users"));
    }

    [Test]
    public async Task StructuralAuditParameters_SurviveTheBelt_SoTheBraceIsNotUndone()
    {
        // Review F1: McpAuditLog.Parameters is Content, but the brace rewrites it into the structural JSON form
        // rather than nulling it. Without the rule's AlreadyRedacted predicate the belt would null it on the same
        // save, so the declared contract ({tool, params[bytes], tables}) would never reach the database.
        using var context = NpgsqlTestContext.Create();
        var structural = McpContentRedactor.StructuralAuditParameters("query", "SELECT sum(revenue) FROM orders", ["orders"]);
        var audit = new McpAuditLog
        {
            ProjectId = ProjectId,
            Tool = "query",
            Parameters = structural,
            ErrorMessage = "permission denied for table orders"
        };
        context.Add(audit);

        await _interceptor.RedactTrackedEntriesAsync(
            context.ChangeTracker,
            (_, _) => Task.FromResult(new ContentRetentionDecision(false, false)));

        audit.Parameters.Should().Be(structural, "the belt leaves a value that is already the redacted form");
        McpContentRedactor.IsStructuralAuditParameters(audit.Parameters).Should().BeTrue();
        audit.ErrorMessage.Should().Be(McpContentRedactor.ClassPermission);
        _logger.Warnings.Should().NotContain(x => x.Contains(nameof(McpAuditLog.Parameters)),
            "nothing was cleared, so nothing is logged");
    }

    [TestCase("{\"tool\":\"ask\",\"params\":[{\"name\":\"input\",\"bytes\":9}],\"tables\":[],\"question\":\"who churned last month\"}", TestName = "extra member smuggling content")]
    [TestCase("{\"tool\":\"ask\",\"params\":[{\"name\":\"who churned last month\",\"bytes\":9}],\"tables\":[]}", TestName = "content in the parameter name")]
    [TestCase("{\"tool\":\"ask\",\"params\":[{\"name\":\"input\",\"bytes\":9}],\"tables\":[{\"q\":\"who churned\"}]}", TestName = "non-string table entry")]
    public async Task AStructuralLookAlikeCarryingContent_IsStillRedacted(string lookAlike)
    {
        // Review N2: the belt's one trust-the-value exemption must accept ONLY the exact shape the redactor emits,
        // or a future write site could smuggle content past it inside an extra member.
        McpContentRedactor.IsStructuralAuditParameters(lookAlike).Should().BeFalse();

        using var context = NpgsqlTestContext.Create();
        var audit = new McpAuditLog { ProjectId = ProjectId, Tool = "ask", Parameters = lookAlike };
        context.Add(audit);

        await _interceptor.RedactTrackedEntriesAsync(
            context.ChangeTracker,
            (_, _) => Task.FromResult(new ContentRetentionDecision(false, false)));

        audit.Parameters.Should().BeNull("a look-alike is not the redacted form, so the belt clears it");
    }

    [Test]
    public async Task EntitiesWithoutAProject_UseTheGlobalDecision()
    {
        // Review F2 (test lane): McpEvalResult and McpSession carry no ProjectId, so they take the interceptor's
        // globalDecision branch — the one the belt's "a future write site cannot leak by omission" promise rests on
        // for eval results. It had no coverage.
        using var context = NpgsqlTestContext.Create();
        var resolvedProjects = new List<int?>();
        var result = new McpEvalResult
        {
            EvalRunId = 1,
            EvalCaseId = 2,
            GeneratedSql = "SELECT sum(revenue) FROM orders",
            ExecutionError = "permission denied for table orders",
            JudgeVerdict = "the generated query double-counts acme",
            ResultRowCount = 4
        };
        context.Add(result);

        await _interceptor.RedactTrackedEntriesAsync(
            context.ChangeTracker,
            (projectId, _) =>
            {
                resolvedProjects.Add(projectId);

                return Task.FromResult(new ContentRetentionDecision(false, false));
            });

        resolvedProjects.Should().Equal([null], "no ProjectId selector means one global resolve");
        result.GeneratedSql.Should().BeNull();
        result.JudgeVerdict.Should().BeNull();
        result.ExecutionError.Should().Be(McpContentRedactor.ClassPermission);
        result.ResultRowCount.Should().Be(4, "counts are structure");
    }

    [Test]
    public async Task ModifiedEntries_AreRedacted_SoAnUpdateCannotReintroduceContent()
    {
        // The braces redact at write time; the belt must also cover a row UPDATED later — e.g. the feedback
        // handler writing a verdict onto a signal that predates the lock. Added-only coverage would miss it.
        using var context = NpgsqlTestContext.Create();
        var signal = NewSignal();
        signal.Id = 11;
        context.Attach(signal);
        signal.FeedbackNote = "customer asked about revenue for acme corp";

        await _interceptor.RedactTrackedEntriesAsync(
            context.ChangeTracker,
            (_, _) => Task.FromResult(new ContentRetentionDecision(false, false)));

        context.Entry(signal).State.Should().Be(EntityState.Modified);
        signal.FeedbackNote.Should().BeNull();
        signal.Question.Should().BeEmpty();
        signal.GeneratedSql.Should().BeNull();
        signal.Tool.Should().Be("ask", "structural properties are never touched");
        _logger.Warnings.Should().OnlyContain(x => !x.Contains("acme"));
    }

    [Test]
    public async Task UnchangedEntries_AreNeverRedacted()
    {
        using var context = NpgsqlTestContext.Create();
        var signal = NewSignal();
        signal.Id = 7;
        context.Attach(signal);

        await _interceptor.RedactTrackedEntriesAsync(
            context.ChangeTracker,
            (_, _) => Task.FromResult(new ContentRetentionDecision(false, false)));

        signal.Question.Should().Be("how many users signed up?");
        _logger.Warnings.Should().BeEmpty();
    }

    private static McpQuerySignal NewSignal() =>
        new()
        {
            ProjectId = ProjectId,
            DataSourceId = 3,
            Tool = "ask",
            Question = "how many users signed up?",
            IntentClassification = "aggregation",
            RoutingDecision = "chose the users source because the question mentions signups",
            GeneratedSql = "select count(*) from users",
            TablesUsed = "public.users",
            ColumnsUsed = "public.users.id",
            ExecutionFailed = true,
            ExecutionError = "relation \"users\" does not exist",
            UserCorrectedSql = "select count(1) from users",
            FeedbackNote = "the analyst meant active users"
        };

    private static McpAuditLog NewAuditLog() =>
        new()
        {
            ProjectId = ProjectId,
            Tool = "query",
            Parameters = "{\"sql\":\"select count(*) from users\"}",
            ErrorMessage = "permission denied for table users"
        };

    private static McpLearnedPattern NewPattern() =>
        new()
        {
            ProjectId = ProjectId,
            DataSourceId = 3,
            SchemaName = "public",
            TableName = "users",
            ColumnName = "email",
            PatternContent = "column public.users.email is an email address",
            ExampleQuestion = "how many users signed up?",
            ExampleSql = "select count(*) from users"
        };

    private sealed class CapturingLogger : ILogger<ContentRetentionInterceptor>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
