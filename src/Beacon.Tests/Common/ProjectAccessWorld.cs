using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Beacon.Tests.Common;

/// <summary>
/// The real project access stack (actor accessor, evaluator, default membership and ceiling sources) over in-memory
/// users, projects, data sources, links and memberships, with user management on — no database (§4.7). Every context
/// reads the lists as they are, so a change made between two DI scopes shows in the second, as a database change shows
/// on the next request. Rows the code adds (audit rows, signals) land in <see cref="Saved"/>.
/// </summary>
internal sealed class ProjectAccessWorld
{
    public List<BeaconUser> Users { get; } = [];

    public List<Project> Projects { get; } = [];

    public List<DataSource> DataSources { get; } = [];

    public List<ProjectDataSource> Links { get; } = [];

    public List<ProjectMember> Members { get; } = [];

    public List<object> Saved { get; } = [];

    /// <summary>Whether the host runs Beacon user management (default: it does).</summary>
    public bool UserManagement { get; set; } = true;

    /// <summary><c>Beacon:Authorization:RequireProjectMembership</c> (default: off).</summary>
    public bool RequireProjectMembership { get; set; }

    private Dictionary<Type, Func<object>> Served { get; } = [];

    public IReadOnlyList<McpAuditLog> AuditLogs => Saved
        .OfType<McpAuditLog>()
        .ToList();

    public BeaconUser User(int id, string externalId, int roleLevel = RoleService.RoleLevels.Editor)
    {
        var user = new BeaconUser
        {
            Id = id,
            ExternalId = externalId,
            UserName = externalId,
            IsEnabled = true,
            UserRoles = [new BeaconUserRole { UserId = id, Role = new BeaconRole { Name = $"Level {roleLevel}", Level = roleLevel } }]
        };
        Users.Add(user);

        return user;
    }

    public Project Project(int id, bool requiresMembership = false)
    {
        var project = new Project { Id = id, Name = $"Project {id}", RequiresMembership = requiresMembership };
        Projects.Add(project);

        return project;
    }

    public DataSource Source(int id, params int[] projectIds)
    {
        var source = new DataSource
        {
            Id = id,
            Name = $"source {id}",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = "encrypted"
        };
        DataSources.Add(source);
        foreach (var projectId in projectIds)
        {
            Links.Add(new ProjectDataSource
            {
                ProjectId = projectId,
                DataSourceId = id,
                Project = Projects.First(x => x.Id == projectId),
                DataSource = source
            });
        }

        return source;
    }

    public ProjectMember Member(int projectId, int userId, ProjectRole role)
    {
        var member = new ProjectMember { ProjectId = projectId, UserId = userId, Role = role };
        Members.Add(member);

        return member;
    }

    /// <summary>Serves <paramref name="rows"/> from every context of the world as well, as they are when it is read.</summary>
    public void Serve<T>(List<T> rows) where T : class
    {
        Served[typeof(T)] = () => RecordingBeaconContext.MemorySet(rows, Saved);
    }

    public IDbContextFactory<BeaconContext> Factory()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(Sets(), Saved));

        return factory.Object;
    }

    /// <summary>
    /// Registers the access stack over this world: the real actor accessor (it reads the registered
    /// <c>IHttpContextAccessor</c>), evaluator and default sources. The authorization provider lets every signed-in
    /// caller write, as an Editor's would.
    /// </summary>
    public void Register(IServiceCollection services)
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = UserManagement;
        configuration.Authorization.Enabled = true;
        var provider = new Mock<IBeaconAuthorizationProvider>();
        provider.Setup(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        provider.Setup(x => x.HasReadPermissionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddSingleton(Factory());
        services.AddSingleton(provider.Object);
        services.AddSingleton(Options.Create(new ProjectAuthorizationOptions { RequireProjectMembership = RequireProjectMembership }));
        services.AddSingleton(new UnmarkedSystemWorkLog(TimeProvider.System, NullLogger<UnmarkedSystemWorkLog>.Instance));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<OwnerRunsAsSystemLog>();
        services.AddScoped<ISystemWorkScope, SystemWorkScope>();
        services.AddScoped<IRunAsScope, RunAsScope>();
        services.AddScoped<IBeaconActorAccessor, BeaconActorAccessor>();
        services.AddSingleton<IProjectMembershipSource, DatabaseProjectMembershipSource>();
        services.AddSingleton<IRunAsCeilingSource, DatabaseRunAsCeilingSource>();
        services.AddScoped<IProjectAccessEvaluator, ProjectAccessEvaluator>();
    }

    private Dictionary<Type, object> Sets()
    {
        var sets = new Dictionary<Type, object>
        {
            [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(Users, Saved),
            [typeof(Project)] = RecordingBeaconContext.MemorySet(Projects, Saved),
            [typeof(DataSource)] = RecordingBeaconContext.MemorySet(DataSources, Saved),
            [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(Links, Saved),
            [typeof(ProjectMember)] = RecordingBeaconContext.MemorySet(Members, Saved),
            [typeof(ProjectDocumentation)] = RecordingBeaconContext.MemorySet(new List<ProjectDocumentation>(), Saved),
            [typeof(GitHubRepository)] = RecordingBeaconContext.MemorySet(new List<GitHubRepository>(), Saved)
        };
        foreach (var served in Served)
        {
            sets[served.Key] = served.Value();
        }

        return sets;
    }
}
