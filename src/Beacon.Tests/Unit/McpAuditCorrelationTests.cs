using System.Diagnostics;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.MCP.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC1 — every audit row carries the correlation ids of its call (W3C trace/span, MCP transport session, upstream
/// request id, API key id), and a caller-supplied request id that is too long or outside the whitelist is dropped.
/// </summary>
[TestFixture]
[NonParallelizable]
public class McpAuditCorrelationTests
{
    private const string SessionId = "sess_abc-123";
    private const string RequestId = "req-42.a:b_c";
    private const string SourceName = "Beacon.Tests.McpAuditCorrelation";

    private static readonly ActivitySource Source = new(SourceName);

    private ActivityListener _listener = null!;

    [SetUp]
    public void SetUp()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = x => x.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [TearDown]
    public void TearDown() => _listener.Dispose();

    [Test]
    public async Task AllSourcesPresent_PopulatesEveryCorrelationColumn()
    {
        using var activity = Source.StartActivity("tools/call");
        activity.Should().NotBeNull("the listener samples every activity of the test source");

        var log = await LogAsync(Context(SessionId, RequestId, apiKeyId: "17"));

        log.TraceId.Should().Be(activity!.TraceId.ToHexString());
        log.TraceId.Should().HaveLength(32);
        log.SpanId.Should().Be(activity.SpanId.ToHexString());
        log.SpanId.Should().HaveLength(16);
        log.McpSessionId.Should().Be(SessionId);
        log.UpstreamRequestId.Should().Be(RequestId);
        log.ApiKeyId.Should().Be(17);
    }

    [Test]
    public async Task NoCurrentActivity_LeavesTraceAndSpanNull()
    {
        Activity.Current = null;

        var log = await LogAsync(Context(SessionId, RequestId, apiKeyId: "17"));

        log.TraceId.Should().BeNull();
        log.SpanId.Should().BeNull();
        log.McpSessionId.Should().Be(SessionId, "the other sources are independent of the activity");
        log.ApiKeyId.Should().Be(17);
    }

    [Test]
    public async Task RequestIdLongerThan128Chars_IsDropped()
    {
        var log = await LogAsync(Context(SessionId, new string('a', 129), apiKeyId: null));

        log.UpstreamRequestId.Should().BeNull();
    }

    [Test]
    public async Task RequestIdOf128Chars_IsKept()
    {
        var requestId = new string('a', 128);

        var log = await LogAsync(Context(SessionId, requestId, apiKeyId: null));

        log.UpstreamRequestId.Should().Be(requestId);
    }

    [TestCase("bad id")]
    [TestCase("inject\nforged=1")]
    [TestCase("trailing-newline\n")]
    [TestCase("<script>")]
    [TestCase("a,b")]
    public async Task RequestIdOutsideWhitelist_IsDropped(string requestId)
    {
        var log = await LogAsync(Context(SessionId, requestId, apiKeyId: null));

        log.UpstreamRequestId.Should().BeNull();
    }

    [Test]
    public async Task CustomRequestIdHeader_IsReadInsteadOfTheDefault()
    {
        var context = Context(SessionId, RequestId, apiKeyId: null);
        context.Request.Headers["X-Correlation-Id"] = "corr-1";

        var log = await LogAsync(context, new McpAuditOptions { RequestIdHeader = "X-Correlation-Id" });

        log.UpstreamRequestId.Should().Be("corr-1");
    }

    [TestCase(null)]
    [TestCase("")]
    public async Task RequestIdHeaderDisabled_LeavesUpstreamRequestIdNull(string? header)
    {
        var log = await LogAsync(Context(SessionId, RequestId, apiKeyId: null), new McpAuditOptions { RequestIdHeader = header });

        log.UpstreamRequestId.Should().BeNull();
        log.McpSessionId.Should().Be(SessionId);
    }

    [TestCase("abc")]
    [TestCase("-5")]
    [TestCase("")]
    public async Task NonNumericApiKeyClaim_LeavesApiKeyIdNull(string claim)
    {
        var log = await LogAsync(Context(SessionId, RequestId, apiKeyId: claim));

        log.ApiKeyId.Should().BeNull();
    }

    [Test]
    public async Task NoHttpContext_LeavesRequestDerivedColumnsNull()
    {
        var log = await LogAsync(null);

        log.McpSessionId.Should().BeNull();
        log.UpstreamRequestId.Should().BeNull();
        log.ApiKeyId.Should().BeNull();
    }

    private static DefaultHttpContext Context(string? sessionId, string? requestId, string? apiKeyId)
    {
        var claims = apiKeyId is null ? new List<Claim>() : [new Claim(McpCallerClaimTypes.ApiKeyId, apiKeyId)];
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey")) };

        if (sessionId is not null)
        {
            context.Request.Headers[McpAuditService.McpSessionIdHeader] = sessionId;
        }

        if (requestId is not null)
        {
            context.Request.Headers[McpAuditOptions.DefaultRequestIdHeader] = requestId;
        }

        return context;
    }

    private static async Task<McpAuditLog> LogAsync(HttpContext? httpContext, McpAuditOptions? audit = null)
    {
        var logs = new List<McpAuditLog>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AuditCapturingContext(logs));

        var service = new McpAuditService(
            factory.Object,
            SettingsProviderMock.Create().Object,
            new HttpContextAccessor { HttpContext = httpContext },
            Options.Create(new McpDeploymentOptions { Audit = audit ?? new McpAuditOptions() }),
            NullLogger<McpAuditService>.Instance, new McpAuditOutcome(), Options.Create(new BeaconTelemetryOptions()), NullLoggerFactory.Instance);

        await service.LogToolCallAsync(
            sessionId: null,
            userId: 1,
            tool: "query",
            parameters: "SELECT 1",
            dataSourceId: 7,
            projectId: 42,
            executionTimeMs: 3,
            resultRowCount: 1,
            errorMessage: null,
            ct: CancellationToken.None);

        logs.Should().ContainSingle();

        return logs[0];
    }

    /// <summary>Captures the audit rows the service adds; no database (§4.7).</summary>
    private sealed class AuditCapturingContext : BeaconContext
    {
        private static readonly DbContextOptions<AuditCapturingContext> ContextOptions =
            new DbContextOptionsBuilder<AuditCapturingContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpAuditLog>> _set = new();

        public AuditCapturingContext(List<McpAuditLog> logs) : base(ContextOptions, "beacon")
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
