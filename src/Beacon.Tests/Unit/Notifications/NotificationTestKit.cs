using System.Security.Claims;
using System.Text.Encodings.Web;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Beacon.Api.Endpoints;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Mcp;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>Shared fixtures for the notification-channel tests: principals, policy, protector and a context over in-memory sets.</summary>
internal static class NotificationTestKit
{
    public static RecipientSecretProtector Protector()
    {
        return new RecipientSecretProtector(new EncryptionService("notification-tests-key"));
    }

    public static NotificationDestinationPolicy Policy(NotificationChannelOptions? options = null)
    {
        var wrapped = Options.Create(options ?? new NotificationChannelOptions());
        return new NotificationDestinationPolicy(wrapped, new OutboundAddressPolicy(wrapped, new FakeResolver()));
    }

    public static RecipientSecretEditor Editor(NotificationChannelOptions? options = null)
    {
        return new RecipientSecretEditor(Policy(options), Protector());
    }

    public static OutboundAddressPolicy AddressPolicy(NotificationChannelOptions? options = null, FakeResolver? resolver = null)
    {
        return new OutboundAddressPolicy(Options.Create(options ?? new NotificationChannelOptions()), resolver ?? new FakeResolver());
    }

    /// <summary>Options allowing <paramref name="entries"/> as private networks for <paramref name="type"/> only.</summary>
    public static NotificationChannelOptions PrivateNetworks(NotificationType type, params string[] entries)
    {
        var options = new NotificationChannelOptions();
        var list = type switch
        {
            NotificationType.Webhook => options.AllowedPrivateNetworks.Webhook,
            NotificationType.Slack => options.AllowedPrivateNetworks.Slack,
            NotificationType.Teams => options.AllowedPrivateNetworks.Teams,
            _ => options.AllowedPrivateNetworks.Jira,
        };
        list.AddRange(entries);
        return options;
    }

    public static IBeaconUserContext UserContext(ClaimsPrincipal user)
    {
        return new HttpContextUserContext(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } });
    }

    public static ClaimsPrincipal Admin()
    {
        return Interactive(RoleService.RoleNames.Admin);
    }

    public static ClaimsPrincipal Interactive(string role)
    {
        Claim[] claims = [new(ClaimTypes.NameIdentifier, "1"), new(ClaimTypes.Role, role)];

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
    }

    /// <summary>The least-privileged API key: Read scope, restricted to an unrelated project, no role.</summary>
    public static ClaimsPrincipal ReadScopedApiKey()
    {
        Claim[] claims =
        [
            new(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod),
            new(McpCallerClaimTypes.Scope, "Read"),
            new("allowed_projects", "[999]"),
            new(ClaimTypes.NameIdentifier, "77"),
        ];

        return new ClaimsPrincipal(new ClaimsIdentity(claims, McpCallerClaimTypes.ApiKeyAuthenticationType));
    }

    /// <summary>
    /// A test server mapping one endpoint area the way <c>MapBeaconApi</c> does (the group's authenticated-user policy and
    /// the real <c>AddBeaconApiAuthorization</c> policies), with every request made as <paramref name="user"/>.
    /// </summary>
    public static async Task<WebApplication> StartApiAsync(
        IMediator mediator,
        ClaimsPrincipal user,
        Func<RouteGroupBuilder, RouteGroupBuilder> map)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(mediator);
        builder.Services.AddRouting();
        builder.Services
            .AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, null);
        builder.Services.AddBeaconApiAuthorization();
        var app = builder.Build();
        app.UseRouting();
        app.Use(async (context, next) =>
        {
            context.User = user;
            await next(context);
        });
        app.UseAuthorization();
        map(app.MapGroup("/beacon/api").RequireAuthorization(BeaconApiEndpoints.AuthPolicyName));
        await app.StartAsync();

        return app;
    }

    public static IDbContextFactory<BeaconContext> Factory(RecipientStore store)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecipientTestContext(store));

        return factory.Object;
    }
}

/// <summary>What the handlers see and write: the recipient rows, entities added and the number of saves.</summary>
internal sealed class RecipientStore
{
    public List<Recipient> Recipients { get; } = [];

    public List<Recipient> Added { get; } = [];

    public int Saves { get; set; }
}

/// <summary>A <see cref="BeaconContext"/> whose recipients are an in-memory async sequence (§4.7: no in-memory provider).</summary>
internal sealed class RecipientTestContext(RecipientStore store) : BeaconContext(ContextOptions, "beacon")
{
    private static readonly DbContextOptions<RecipientTestContext> ContextOptions =
        new DbContextOptionsBuilder<RecipientTestContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseSnakeCaseNamingConvention()
            .Options;

    public override DbSet<TEntity> Set<TEntity>() where TEntity : class
    {
        if (typeof(TEntity) == typeof(Recipient))
        {
            return (DbSet<TEntity>)(object)RecipientSet();
        }

        return base.Set<TEntity>();
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        store.Saves++;
        return Task.FromResult(1);
    }

    private DbSet<Recipient> RecipientSet()
    {
        var data = store.Recipients.AsQueryable();
        var set = new Mock<DbSet<Recipient>>();
        set.As<IAsyncEnumerable<Recipient>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<Recipient>(data.GetEnumerator()));
        set.As<IQueryable<Recipient>>()
            .Setup(x => x.Provider)
            .Returns(new TestAsyncQueryProvider<Recipient>(data.Provider));
        set.As<IQueryable<Recipient>>().Setup(x => x.Expression).Returns(data.Expression);
        set.As<IQueryable<Recipient>>().Setup(x => x.ElementType).Returns(data.ElementType);
        set.As<IQueryable<Recipient>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());

        // GetRecipientsHandler calls DbSet<T>.AsQueryable() (a virtual instance method); without this Moq would return
        // its default empty, non-async queryable instead of the seeded set.
        set.Setup(x => x.AsQueryable()).Returns(() => set.Object);
        set.Setup(x => x.Add(It.IsAny<Recipient>())).Callback<Recipient>(store.Added.Add);
        return set.Object;
    }
}

/// <summary>Authenticates nobody itself (the test sets the user); answers a forbidden request with 403.</summary>
internal sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        return Task.FromResult(AuthenticateResult.NoResult());
    }
}

/// <summary>
/// A resolver over a fixed table (or, per host, a queue of successive answers); an unknown host fails the way DNS does.
/// </summary>
internal sealed class FakeResolver : Dictionary<string, string[]>, IHostAddressResolver
{
    private readonly Dictionary<string, Queue<string[]>> _sequences = new(StringComparer.OrdinalIgnoreCase);

    public FakeResolver()
        : base(StringComparer.OrdinalIgnoreCase)
    {
    }

    public int Lookups { get; private set; }

    /// <summary>Answers <paramref name="host"/> with each of <paramref name="answers"/> in turn, then the last one.</summary>
    public FakeResolver Sequence(string host, params string[][] answers)
    {
        _sequences[host] = new Queue<string[]>(answers);
        return this;
    }

    public Task<System.Net.IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        Lookups++;
        string[]? addresses;
        if (_sequences.TryGetValue(host, out var queue) && queue.Count > 0)
        {
            addresses = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        }
        else if (!TryGetValue(host, out addresses))
        {
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
        }

        return Task.FromResult(addresses.Select(System.Net.IPAddress.Parse).ToArray());
    }
}

/// <summary>Collects every log entry (level, rendered message, exception) for assertions on what is and is not logged.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    /// <summary>Everything logged at <paramref name="level"/> or above, messages and exception texts together.</summary>
    public string TextAtOrAbove(LogLevel level)
    {
        return string.Join("\n", Entries
            .Where(x => x.Level >= level)
            .Select(x => x.Message + " " + x.Exception));
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception), exception));
    }
}

/// <summary>
/// Runs EF on a real provider without a database: connections are never opened, every command is captured, reads
/// return <see cref="Rows"/> (columns by position) and writes report <see cref="RowsAffected"/> (or throw
/// <see cref="FailingWrite"/> on the given write, counting from 1).
/// </summary>
internal sealed class CommandCapture : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor, Microsoft.EntityFrameworkCore.Diagnostics.IDbConnectionInterceptor
{
    public List<(string Text, List<object?> Parameters)> Commands { get; } = [];

    public List<object?[]> Rows { get; init; } = [];

    public string[] Columns { get; init; } = ["c0", "c1", "c2"];

    public int RowsAffected { get; init; } = 1;

    public int? FailingWrite { get; init; }

    public int Writes { get; private set; }

    public ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> ConnectionOpeningAsync(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult.Suppress());
    }

    public Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult ConnectionOpening(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result)
    {
        return Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult.Suppress();
    }

    public ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> ConnectionClosingAsync(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result)
    {
        return ValueTask.FromResult(Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult.Suppress());
    }

    public Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult ConnectionClosing(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result)
    {
        return Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult.Suppress();
    }

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
        System.Data.Common.DbCommand command,
        Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Capture(command);
        var table = new System.Data.DataTable();
        var width = Rows.Count == 0 ? Columns.Length : Rows[0].Length;
        for (var i = 0; i < width; i++)
        {
            var type = Rows.Select(x => x[i]?.GetType()).FirstOrDefault(x => x != null) ?? typeof(string);
            table.Columns.Add(i < Columns.Length ? Columns[i] : $"c{i}", type);
        }

        foreach (var row in Rows)
        {
            table.Rows.Add(row.Select(x => x ?? DBNull.Value).ToArray());
        }

        return ValueTask.FromResult(Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>.SuppressWithResult(table.CreateDataReader()));
    }

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
        System.Data.Common.DbCommand command,
        Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Capture(command);
        Writes++;
        if (Writes == FailingWrite)
        {
            throw new InvalidOperationException("simulated write failure");
        }

        return ValueTask.FromResult(Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>.SuppressWithResult(RowsAffected));
    }

    public IDbContextFactory<BeaconContext> NpgsqlFactory()
    {
        var options = new DbContextOptionsBuilder<NpgsqlTestContext>()
            .UseNpgsql("Host=localhost;Database=test_does_not_exist")
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(this)
            .Options;

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NpgsqlTestContext(options));

        return factory.Object;
    }

    private void Capture(System.Data.Common.DbCommand command)
    {
        Commands.Add((
            command.CommandText,
            command.Parameters
                .Cast<System.Data.Common.DbParameter>()
                .Select(x => x.Value is DBNull ? null : x.Value)
                .ToList()));
    }
}
