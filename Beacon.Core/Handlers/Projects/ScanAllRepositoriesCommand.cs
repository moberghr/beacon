using MediatR;

namespace Beacon.Core.Handlers.Projects;

public record ScanAllRepositoriesCommand(int ProjectId) : IRequest<ScanAllRepositoriesResult>;

public record ScanAllRepositoriesResult(int ScannedCount, List<string> Errors);
