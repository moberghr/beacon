using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Beacon.Tests.Common;

/// <summary>
/// Beacon's own shared-source tightening (<see cref="SharedSourceSettings"/>) over a world where every project of
/// <c>projectSettings</c> is live and links the data source, each resolved to its own settings by a project-aware
/// settings provider. No database (§4.7).
/// </summary>
internal static class TestSharedSources
{
    public static ISharedSourceSettings Linking(int dataSourceId, IReadOnlyDictionary<int, McpSettingsData> projectSettings, McpSettingsData? global = null)
    {
        var projects = projectSettings.Keys
            .Select(x => Project(x, dataSourceId))
            .ToList();
        var links = projects
            .SelectMany(x => x.DataSources)
            .ToList();
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(Project)] = RecordingBeaconContext.MemorySet(projects, saved),
                    [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(links, saved)
                },
                saved));

        return new SharedSourceSettings(factory.Object, SettingsProviderMock.Create(global, projectSettings: projectSettings).Object);
    }

    private static Project Project(int id, int dataSourceId)
    {
        var project = new Project { Id = id, Name = $"project-{id}" };
        project.DataSources = [new ProjectDataSource { ProjectId = id, Project = project, DataSourceId = dataSourceId }];

        return project;
    }
}
