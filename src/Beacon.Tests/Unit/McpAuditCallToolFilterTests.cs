using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Telemetry;
using Beacon.MCP.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC7 — fail-closed: Required + Failed withholds the result (IsError + 9101), and so does Required + a successful
/// result with no audit row written at all; Required + Written, Required + an unaudited error result and
/// not-Required + Failed pass the tool's result through unchanged. SC8 — the filter is part of the production
/// <c>AddBeaconMcp</c> registration and wraps a custom <c>CallToolHandler</c> (the saved-query / host-endpoint path).
/// </summary>
[TestFixture]
public class McpAuditCallToolFilterTests
{
    private const string ToolName = "api_orders";
    private const string ToolText = "rows from the tool";

    private static readonly CallToolResult ToolResult = new()
    {
        Content = [new TextContentBlock { Text = ToolText }]
    };

    [Test]
    public async Task RequiredAndAuditFailed_WithholdsTheResultAndLogs9101()
    {
        var logs = new List<(string Category, LogLevel Level, EventId EventId, string Message)>();
        var services = Services(required: true, new McpAuditOutcome { Failed = true }, logs);
        var toolRan = false;

        var result = await InvokeAsync(McpAuditCallToolFilter.Create(), services, () => toolRan = true);

        toolRan.Should().BeTrue("the filter replaces the result after the tool ran; audit and signal are never skipped");
        result.IsError.Should().BeTrue();
        result.Content.Should().ContainSingle()
            .Which.Should().BeOfType<TextContentBlock>()
            .Which.Text.Should().Be(McpAuditCallToolFilter.WithheldMessage);
        result.Content.OfType<TextContentBlock>().Should().NotContain(x => x.Text == ToolText);

        var withheld = logs.Should().ContainSingle().Subject;
        withheld.Category.Should().Be(BeaconTelemetry.AuditLogCategory);
        withheld.Level.Should().Be(LogLevel.Warning);
        withheld.EventId.Id.Should().Be(9101);
        withheld.EventId.Name.Should().Be("McpToolResultWithheld");
        withheld.Message.Should().Contain(ToolName);
    }

    [Test]
    public async Task RequiredAndAuditWritten_ReturnsTheToolResultUnchanged()
    {
        var logs = new List<(string Category, LogLevel Level, EventId EventId, string Message)>();
        var services = Services(required: true, new McpAuditOutcome { Written = true }, logs);

        var result = await InvokeAsync(McpAuditCallToolFilter.Create(), services);

        result.Should().BeSameAs(ToolResult);
        logs.Should().BeEmpty();
    }

    [Test]
    public async Task NotRequiredAndAuditFailed_ReturnsTheToolResultUnchanged()
    {
        var logs = new List<(string Category, LogLevel Level, EventId EventId, string Message)>();
        var services = Services(required: false, new McpAuditOutcome { Failed = true }, logs);

        var result = await InvokeAsync(McpAuditCallToolFilter.Create(), services);

        result.Should().BeSameAs(ToolResult);
        logs.Should().BeEmpty();
    }

    [Test]
    public async Task RequiredAndNothingAudited_SuccessResult_IsWithheld()
    {
        var logs = new List<(string Category, LogLevel Level, EventId EventId, string Message)>();
        var services = Services(required: true, new McpAuditOutcome(), logs);

        var result = await InvokeAsync(McpAuditCallToolFilter.Create(), services);

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Should().ContainSingle()
            .Which.Text.Should().Be(McpAuditCallToolFilter.WithheldMessage);
        logs.Should().ContainSingle().Which.EventId.Id.Should().Be(9101);
    }

    [Test]
    public async Task RequiredAndNothingAudited_ErrorResult_PassesThroughUnchanged()
    {
        var logs = new List<(string Category, LogLevel Level, EventId EventId, string Message)>();
        var services = Services(required: true, new McpAuditOutcome(), logs);
        var errorResult = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "Unknown tool: q_whatever" }]
        };
        var handler = McpAuditCallToolFilter.Create()((_, _) => ValueTask.FromResult(errorResult));

        var result = await handler(Request(services), CancellationToken.None);

        result.Should().BeSameAs(errorResult, "an unaudited error result carries no data");
        logs.Should().BeEmpty();
    }

    [Test]
    public async Task RequiredAndAuditFailed_ErrorResult_IsStillWithheld()
    {
        var services = Services(required: true, new McpAuditOutcome { Failed = true }, []);
        var handler = McpAuditCallToolFilter.Create()((_, _) =>
            ValueTask.FromResult(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "boom" }] }));

        var result = await handler(Request(services), CancellationToken.None);

        result.Content.OfType<TextContentBlock>().Should().ContainSingle()
            .Which.Text.Should().Be(McpAuditCallToolFilter.WithheldMessage);
    }

    [Test]
    public void AuditOutcome_IsRegisteredScoped()
    {
        var registration = new ServiceCollection();
        Beacon.MCP.ServiceConfiguration.AddBeaconMcp(registration);

        registration.Should().ContainSingle(x => x.ServiceType == typeof(McpAuditOutcome))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped,
                "the SDK filter and the audit service must share one outcome per tools/call scope");
    }

    [Test]
    public async Task RegisteredPipeline_RealAuditServiceInTheRequestScope_FailedSaveIsWithheld()
    {
        var registration = new ServiceCollection();
        registration.AddLogging();
        Beacon.MCP.ServiceConfiguration.AddBeaconMcp(registration);

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new FailingSaveContext());
        registration.AddSingleton(factory.Object);
        registration.AddSingleton(SettingsProviderMock.Create().Object);
        registration.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        registration.Configure<McpDeploymentOptions>(x => x.Audit = new McpAuditOptions { Required = true });

        // The custom handler audits through the McpAuditService resolved from the request's scope — the same scope
        // the filter reads the McpAuditOutcome from. A lifetime or scope mismatch would let the result through.
        registration.Configure<McpServerOptions>(x =>
        {
            var previous = x.Handlers.CallToolHandler;
            x.Handlers.CallToolHandler = async (request, cancellationToken) =>
            {
                if (request.Params?.Name != ToolName)
                {
                    return await previous!(request, cancellationToken);
                }

                var audit = request.Services!.GetRequiredService<McpAuditService>();
                await audit.LogToolCallAsync(null, 1, ToolName, null, null, 42, 5, 3, null, ct: cancellationToken);

                return ToolResult;
            };
        });

        using var provider = registration.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var pipeline = options.Handlers.CallToolHandler!;
        for (var i = options.Filters.Request.CallToolFilters.Count - 1; i >= 0; i--)
        {
            pipeline = options.Filters.Request.CallToolFilters[i](pipeline);
        }

        using var scope = provider.CreateScope();
        var result = await pipeline(Request(scope.ServiceProvider), CancellationToken.None);

        scope.ServiceProvider.GetRequiredService<McpAuditOutcome>().Failed.Should().BeTrue();
        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Should().ContainSingle()
            .Which.Text.Should().Be(McpAuditCallToolFilter.WithheldMessage);
    }

    [Test]
    public async Task NoRequestServices_ReturnsTheToolResultUnchanged()
    {
        var result = await InvokeAsync(McpAuditCallToolFilter.Create(), services: null);

        result.Should().BeSameAs(ToolResult);
    }

    [Test]
    public async Task ToolThrows_ExceptionPropagatesUnchanged()
    {
        var services = Services(required: true, new McpAuditOutcome { Failed = true }, []);
        var handler = McpAuditCallToolFilter.Create()((_, _) => throw new InvalidOperationException("tool failed"));

        var act = async () => await handler(Request(services), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("tool failed");
    }

    [Test]
    public async Task RegisteredFilter_WrapsCustomCallToolHandler()
    {
        var registration = new ServiceCollection();
        registration.AddLogging();
        Beacon.MCP.ServiceConfiguration.AddBeaconMcp(registration);

        // A custom handler chained the way AddHostEndpointTools / the saved-query tools chain theirs.
        var customRan = false;
        registration.Configure<McpServerOptions>(x =>
        {
            var previous = x.Handlers.CallToolHandler;
            x.Handlers.CallToolHandler = (request, cancellationToken) =>
            {
                if (request.Params?.Name == ToolName)
                {
                    customRan = true;

                    return ValueTask.FromResult(ToolResult);
                }

                return previous!(request, cancellationToken);
            };
        });

        using var provider = registration.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        options.Filters.Request.CallToolFilters.Should().ContainSingle("AddBeaconMcp registers exactly one call-tool filter");

        // Compose exactly as the SDK does (McpServerImpl.BuildFilterPipeline: last filter innermost).
        var pipeline = options.Handlers.CallToolHandler!;
        for (var i = options.Filters.Request.CallToolFilters.Count - 1; i >= 0; i--)
        {
            pipeline = options.Filters.Request.CallToolFilters[i](pipeline);
        }

        var failed = Services(required: true, new McpAuditOutcome { Failed = true }, []);
        var withheld = await pipeline(Request(failed), CancellationToken.None);

        customRan.Should().BeTrue();
        withheld.IsError.Should().BeTrue();
        withheld.Content.OfType<TextContentBlock>().Should().ContainSingle()
            .Which.Text.Should().Be(McpAuditCallToolFilter.WithheldMessage);

        var written = Services(required: true, new McpAuditOutcome { Written = true }, []);
        var passed = await pipeline(Request(written), CancellationToken.None);

        passed.Should().BeSameAs(ToolResult);
    }

    private static async Task<CallToolResult> InvokeAsync(
        McpRequestFilter<CallToolRequestParams, CallToolResult> filter, IServiceProvider? services, Action? onRun = null)
    {
        var handler = filter((_, _) =>
        {
            onRun?.Invoke();

            return ValueTask.FromResult(ToolResult);
        });

        return await handler(Request(services), CancellationToken.None);
    }

    private static RequestContext<CallToolRequestParams> Request(IServiceProvider? services) =>
        new(Mock.Of<McpServer>(), new JsonRpcRequest { Method = "tools/call" }, new CallToolRequestParams { Name = ToolName })
        {
            Services = services
        };

    private static ServiceProvider Services(bool required, McpAuditOutcome outcome,
        List<(string Category, LogLevel Level, EventId EventId, string Message)> logs)
    {
        return new ServiceCollection()
            .AddSingleton(outcome)
            .AddSingleton(Options.Create(new McpDeploymentOptions { Audit = new McpAuditOptions { Required = required } }))
            .AddSingleton<ILoggerFactory>(new LoggerFactory([new ListLoggerProvider(logs)]))
            .BuildServiceProvider();
    }

    /// <summary>Accepts the audit row, then fails the save — no database (§4.7).</summary>
    private sealed class FailingSaveContext : BeaconContext
    {
        private static readonly DbContextOptions<FailingSaveContext> ContextOptions =
            new DbContextOptionsBuilder<FailingSaveContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpAuditLog>> _set = new();

        public FailingSaveContext() : base(ContextOptions, "beacon")
        {
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpAuditLog))
            {
                return (DbSet<TEntity>)(object)_set.Object;
            }

            return base.Set<TEntity>();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("audit sink unavailable");
    }

    private sealed class ListLoggerProvider(List<(string Category, LogLevel Level, EventId EventId, string Message)> logs)
        : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, logs);

        public void Dispose()
        {
        }
    }

    private sealed class ListLogger(string category, List<(string Category, LogLevel Level, EventId EventId, string Message)> logs)
        : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            logs.Add((category, logLevel, eventId, formatter(state, exception)));
    }
}
