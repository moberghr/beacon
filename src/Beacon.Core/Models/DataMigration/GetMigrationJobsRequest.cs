using Beacon.Core.Helpers;

namespace Beacon.Core.Models.DataMigration;

public record GetMigrationJobsRequest : ListRequest
{
    public int? DataSourceId { get; init; }
    public bool? IsEnabled { get; init; }
    public bool IncludeArchived { get; init; } = false;
    public string? SearchTerm { get; init; }
}