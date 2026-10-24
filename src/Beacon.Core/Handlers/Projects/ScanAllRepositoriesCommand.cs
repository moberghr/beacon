using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Projects;

public record ScanAllRepositoriesCommand(int ProjectId) : IRequest<ScanAllRepositoriesResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Project(ProjectId), ProjectAction.Write)
    ];
}

public record ScanAllRepositoriesResult(int ScannedCount, List<string> Errors)
{
    /// <summary>The scanned repositories whose scan hit a limit (files, file size or time): their references are incomplete.</summary>
    public List<RepositoryPartialScan> PartialScans { get; init; } = [];
}

/// <summary>A repository whose scan completed but hit a limit, and why.</summary>
public record RepositoryPartialScan(int RepositoryId, List<string> Reasons);
