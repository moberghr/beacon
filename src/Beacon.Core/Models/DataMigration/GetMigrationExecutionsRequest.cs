using Beacon.Core.Authorization;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;

namespace Beacon.Core.Models.DataMigration;

/// <summary>
/// Newest first unless <c>sort</c> names <c>startedAt</c>, <c>completedAt</c>, <c>status</c> or
/// <c>sourceRowsRead</c> (optionally <c>-</c> prefixed).
/// </summary>
public record GetMigrationExecutionsRequest : ListRequest
{
    public int? MigrationJobId { get; init; }

    public MigrationStatus? Status { get; init; }

    public DateTime? StartDate { get; init; }

    public DateTime? EndDate { get; init; }

    /// <summary>
    /// The projects the caller reads: only runs of jobs whose source and destination data sources it reads are listed.
    /// Null lists every run.
    /// </summary>
    public ProjectSet? Readable { get; init; }
}
