using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Retention;
using Beacon.MCP.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Review finding F3 (Stage 2 test lane): §1.7 says an MCP tool call is audited whatever else happens, and Stage 1
/// found that the new settings read could abort the write and lose the row. The fix (a fail-closed
/// <c>RetainsContentAsync</c>) had no regression test, so removing it would silently reintroduce exactly the defect
/// that was already found once. These pin the three outcomes: locked, unlocked, and settings-unavailable.
/// </summary>
[TestFixture]
public class McpAuditServiceRetentionTests
{
    private const int ProjectId = 42;
    private const string Sql = "SELECT sum(revenue) FROM orders WHERE customer = 'acme'";

    [Test]
    public async Task SettingsUnavailable_StillWritesTheRow_Structurally()
    {
        var provider = new Mock<IMcpSettingsProvider>();
        provider
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("settings store unavailable"));

        var logs = await LogAsync(provider);

        logs.Should().ContainSingle("§1.7 — the audit row is written whatever happens to the settings read");
        logs[0].Parameters.Should().NotContain("acme", "fail closed: unknown decision means no content");
        McpContentRedactor.IsStructuralAuditParameters(logs[0].Parameters).Should().BeTrue();
        logs[0].ErrorMessage.Should().Be(McpContentRedactor.ClassPermission);
    }

    [Test]
    public async Task Locked_WritesStructuralParameters_AndTheErrorClass()
    {
        var logs = await LogAsync(SettingsProviderMock.Create(new McpSettingsData { RetainQueryContent = false }));

        logs.Should().ContainSingle();
        logs[0].Parameters.Should().NotContain("acme");
        using var document = JsonDocument.Parse(logs[0].Parameters!);
        document.RootElement.GetProperty("tool").GetString().Should().Be("query");
        document.RootElement.GetProperty("tables").EnumerateArray().Select(x => x.GetString()).Should().Equal("orders");
        logs[0].ErrorMessage.Should().Be(McpContentRedactor.ClassPermission);
    }

    [Test]
    public async Task Unlocked_KeepsTheParametersAndTheErrorText()
    {
        // The over-redaction direction: a change that redacted unconditionally would destroy audit detail on every
        // deployment that never turned the lock on.
        var logs = await LogAsync(SettingsProviderMock.Create(new McpSettingsData { RetainQueryContent = true }));

        logs.Should().ContainSingle();
        logs[0].Parameters.Should().Be(Sql);
        logs[0].ErrorMessage.Should().Be("permission denied for table orders");
    }

    private static async Task<List<McpAuditLog>> LogAsync(Mock<IMcpSettingsProvider> provider)
    {
        var logs = new List<McpAuditLog>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AuditCapturingContext(logs));

        var service = new McpAuditService(factory.Object, provider.Object, NullLogger<McpAuditService>.Instance);

        await service.LogToolCallAsync(
            sessionId: null,
            userId: 1,
            tool: "query",
            parameters: Sql,
            dataSourceId: 7,
            projectId: ProjectId,
            executionTimeMs: 12,
            resultRowCount: null,
            errorMessage: "permission denied for table orders",
            tables: ["orders"],
            ct: CancellationToken.None);

        return logs;
    }

    /// <summary>Captures the audit rows the service adds; no database (§4.7).</summary>
    private sealed class AuditCapturingContext : BeaconContext
    {
        private static readonly DbContextOptions<AuditCapturingContext> Options =
            new DbContextOptionsBuilder<AuditCapturingContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpAuditLog>> _set = new();

        public AuditCapturingContext(List<McpAuditLog> logs) : base(Options, "beacon")
        {
            _set.Setup(x => x.Add(It.IsAny<McpAuditLog>())).Callback<McpAuditLog>(logs.Add);
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpAuditLog))
            {
                return (DbSet<TEntity>)(object)_set.Object;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
