using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Metadata;

namespace Beacon.Core.Services.Providers;

public interface IDatabaseMetadataExtractor
{
    DatabaseEngineType SupportedEngineType { get; }
    Task<IReadOnlyList<TableMetadataDto>> ExtractMetadataAsync(string connectionString, CancellationToken cancellationToken = default);
}
