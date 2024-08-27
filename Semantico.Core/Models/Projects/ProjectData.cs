using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.Projects
{
    public class ProjectData
    {
        public int? ProjectId { get; init; }

        public required string Name { get; init; }

        public required string ConnectionString { get; init; }

        public required DatabaseEngineType DatabaseEngineType { get; init; }
    }
}
