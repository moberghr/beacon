using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Queries;

namespace Semantico.Core.Models.Projects;

public class ProjectListData
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    public required DatabaseEngineType DatabaseEngineType { get; init; }

    public List<QueryData> Queries { get; init; } = new();
}