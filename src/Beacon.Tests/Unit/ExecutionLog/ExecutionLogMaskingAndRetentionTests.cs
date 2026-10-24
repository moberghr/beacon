using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Retention;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ExecutionLog;

/// <summary>
/// The execution log's SQL text as readers get it (values masked through the dialect's tokenizer, conservatively when the
/// dialect is unknown) and its retention purge (a no-op while unset, rows older than the window otherwise). No database.
/// </summary>
[TestFixture]
public class ExecutionLogMaskingAndRetentionTests
{
    private static readonly string? PostgreSql = DataSourceSqlDialect.Of(DataSourceType.Database, DatabaseEngineType.PostgreSQL);

    private static readonly string? MySql = DataSourceSqlDialect.Of(DataSourceType.Database, DatabaseEngineType.MySQL);

    private static readonly string? SqlServer = DataSourceSqlDialect.Of(DataSourceType.Database, DatabaseEngineType.MSSQL);

    [Test]
    public void MaskLiterals_ReplacesStringsAndNumbers_KeepsNamesAndBindMarkers()
    {
        var masked = SqlCommentStripper.MaskLiterals(
            "SELECT \"Email\", amount * 1.5 FROM accounts WHERE email = 'ana@example.com' AND id = $1 AND body = $$x$$ LIMIT 11",
            PostgreSql);

        masked.Should().Be("SELECT \"Email\", amount * ? FROM accounts WHERE email = ? AND id = $1 AND body = ? LIMIT ?");
        SqlCommentStripper.MaskLiterals("SELECT a FROM t WHERE e = E'ana@example.com'", PostgreSql).Should().NotContain("ana");
    }

    [Test]
    public void MaskLiterals_RemovesComments()
    {
        var masked = SqlCommentStripper.MaskLiterals("SELECT 1 -- ana@example.com\nFROM t /* id 42 */", PostgreSql);

        masked.Should().NotContain("ana@example.com").And.NotContain("42").And.Contain("FROM t");
    }

    [Test]
    public void MaskLiterals_ReadsTheDialectsOwnLiterals()
    {
        SqlCommentStripper.MaskLiterals("SELECT `name` FROM t WHERE a = \"ana\" AND b = 'it''s'", MySql)
            .Should().Be("SELECT `name` FROM t WHERE a = ? AND b = ?");
        SqlCommentStripper.MaskLiterals("SELECT [name] FROM t WHERE a = N'ana' AND b = @p0", SqlServer)
            .Should().Be("SELECT [name] FROM t WHERE a = ? AND b = @p0");
    }

    [TestCase(null)]
    [TestCase("no-such-dialect")]
    public void MaskLiterals_WithoutAKnownDialect_MasksEveryQuotedSpanAndNumber(string? dialect)
    {
        var masked = SqlCommentStripper.MaskLiterals(
            "SELECT t1.a FROM t1 WHERE x = 'it''s \\' still' AND y = \"ana\" AND z = 42 AND w = $tag$v$tag$ AND v = $1 -- 7\n AND u = 'open",
            dialect);

        masked.Should().Be("SELECT t1.a FROM t1 WHERE x = ? AND y = ? AND z = ? AND w = ? AND v = $1  \n AND u = ?");
    }

    [Test]
    public void MaskLiterals_SqlTheTokenizerCannotRead_IsMaskedConservatively()
    {
        var masked = SqlCommentStripper.MaskLiterals("SELECT 'ana@example.com' FROM t WHERE x = 'open", PostgreSql);

        masked.Should().Be("SELECT ? FROM t WHERE x = ?");
    }

    [Test]
    public async Task ThePurge_WithoutARetention_IsANoOp()
    {
        var capture = new SqlCapture();
        var service = Purge(capture, retentionDays: null);

        var deleted = await service.PurgeExpiredAsync(CancellationToken.None);

        deleted.Should().Be(0);
        capture.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task ThePurge_RemovesTheRowsOlderThanTheWindow()
    {
        var capture = new SqlCapture().ThenRowsAffected(3);
        var service = Purge(capture, retentionDays: 30);

        var deleted = await service.PurgeExpiredAsync(CancellationToken.None);

        deleted.Should().Be(3);
        capture.Commands.Should().ContainSingle()
            .Which.Should().Contain("DELETE FROM beacon.manual_query_execution_logs").And.Contain("created_time <");
        capture.CommandParameters.Single().Values.Should().Contain(new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void ANonPositiveRetention_IsRefused(int days)
    {
        var result = new SecurityAuditOptionsValidator().Validate(null, new SecurityAuditOptions { ExecutionLog = new ExecutionLogOptions { RetentionDays = days } });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Beacon:Audit:ExecutionLog:RetentionDays");
    }

    private static ExecutionLogRetentionService Purge(SqlCapture capture, int? retentionDays) =>
        new(
            capture.Factory(),
            Options.Create(new SecurityAuditOptions { ExecutionLog = new ExecutionLogOptions { RetentionDays = retentionDays } }),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero)),
            NullLoggerFactory.Instance);
}
