using System.Security.Claims;
using Beacon.AI;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Worker;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Beacon.Tests.Common;

/// <summary>
/// Sends requests through the real Beacon Core and AI registrations (MediatR pipeline, handlers, services) with the
/// database replaced by a <see cref="SqlCapture"/> that answers every count with one and every other read with no rows,
/// and project access fixed to a given scope (and no functional permission): what each handler sends to the database,
/// without one (§4.7). The by-id
/// checks a request also declares resolve every resource into project 1, so they pass for a scope that reads project 1
/// and the handler's own SQL is what is captured.
/// </summary>
internal sealed class ListHandlerHarness
{
    private readonly ProjectAccessScope _scope;
    private readonly bool _sqlServer;

    /// <param name="sqlServer">Capture on the SQL Server provider instead of Npgsql, for a translation's SQL Server twin.</param>
    public ListHandlerHarness(ProjectAccessScope scope, bool sqlServer = false)
    {
        _scope = scope;
        _sqlServer = sqlServer;
    }

    /// <summary>Why the last request stopped early (no row to read), if it did.</summary>
    public string? LastStop { get; private set; }

    public async Task<IReadOnlyList<string>> SendAsync(object request)
    {
        return (await CaptureAsync(request)).Commands;
    }

    /// <summary>As <see cref="SendAsync"/>, with each command's parameters.</summary>
    public async Task<SqlCapture> CaptureAsync(object request)
    {
        var capture = (_sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture()).AnsweringCountsWithOne();
        await using var provider = Build(capture.Factory());
        await using var scope = provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        try
        {
            await mediator.Send(request);
        }
        catch (Exception ex) when (capture.Commands.Count > 0)
        {
            // The scripted answers carry no rows; a handler that needs one stops here, after its SQL was captured.
            LastStop = ex.GetType().Name;
        }

        return capture;
    }

    /// <summary>
    /// Sends <paramref name="request"/> to its real handler with the database replaced by <paramref name="factory"/> (in-memory
    /// sets, never a connection), and returns its answer: what a list handler keeps of rows a test lays out.
    /// <paramref name="services"/> adds registrations a host would make (a connector's services, for one).
    /// </summary>
    public async Task<object?> AnswerAsync(object request, IDbContextFactory<BeaconContext> factory, Action<IServiceCollection>? services = null)
    {
        await using var provider = Build(factory, services);
        await using var scope = provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    private ServiceProvider Build(IDbContextFactory<BeaconContext> factory, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:EncryptionKey"] = TestEncryptionKey.Value
            })
            .Build();
        services.AddLogging(x => x.ClearProviders());
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());
        services.AddBeaconAI(configuration);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-ada")], "Cookies"))
            }
        });
        services.AddScoped<IBeaconUserContext, HttpContextUserContext>();
        services.AddSingleton(factory);
        services.AddScoped(_ => TestProjectAccess.Evaluator(_scope));
        services.AddScoped(_ => TestPermissions.None);
        services.AddScoped<IProjectResourceResolver, InProjectOne>();
        configure?.Invoke(services);

        return services.BuildServiceProvider();
    }

    private sealed class InProjectOne : IProjectResourceResolver
    {
        public Task<ResolvedProjectResource> ResolveAsync(BeaconContext context, ProjectResource resource, CancellationToken cancellationToken) =>
            Task.FromResult(ResolvedProjectResource.InProject(1));
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
