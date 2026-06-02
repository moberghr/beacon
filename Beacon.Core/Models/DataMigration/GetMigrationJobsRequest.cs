using Beacon.Core.Helpers;

namespace Beacon.Core.Models.DataMigration;

public class GetMigrationJobsRequest : SortedListRequest
{
    public int? DataSourceId { get; set; }
    public bool? IsEnabled { get; set; }
    public bool IncludeArchived { get; set; } = false;
    public string? SearchTerm { get; set; }
}