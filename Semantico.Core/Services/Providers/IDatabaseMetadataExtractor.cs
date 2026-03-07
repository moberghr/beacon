using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Metadata;

namespace Semantico.Core.Services.Providers;

public interface IDatabaseMetadataExtractor
{
    DatabaseEngineType SupportedEngineType { get; }
    Task<IReadOnlyList<TableMetadataDto>> ExtractMetadataAsync(string connectionString, CancellationToken cancellationToken = default);
}
