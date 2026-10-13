using System.Text.Json;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Approvals;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// What a migration job does when it runs, and so what an approval of it covers: the source data source, the source query
/// (SQL or JSON steps, character for character), the destination data source and table, the write mode and the
/// transformation script. Its name, description and the settings no run reads (schedule, retries, timeout, validation flag,
/// enabled) are left out.
/// </summary>
internal sealed record MigrationJobDefinition(
    int SourceDataSourceId,
    string QueryText,
    int DestinationDataSourceId,
    string DestinationTable,
    MigrationMode Mode,
    string? TransformationScript)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>The SHA-256 the job's approval records and every run compares.</summary>
    public string Hash => ChangeApprovalHashes.Sha256(
        "beacon:migration-job:v1\n"
        + JsonSerializer.Serialize(
            new
            {
                SourceDataSourceId,
                QueryText,
                DestinationDataSourceId,
                DestinationTable,
                Mode,
                TransformationScript
            },
            JsonOptions));

    public static MigrationJobDefinition Of(MigrationJob job)
    {
        return new MigrationJobDefinition(
            job.DataSourceId,
            job.QueryText,
            job.DestinationDataSourceId,
            job.DestinationTable,
            job.Mode,
            job.TransformationScript);
    }

    /// <summary>The definition as a reviewer is shown it (the data source names are filled in when it is shown).</summary>
    public MigrationJobDefinitionView View()
    {
        return new MigrationJobDefinitionView
        {
            SourceDataSourceId = SourceDataSourceId,
            QueryText = QueryText,
            DestinationDataSourceId = DestinationDataSourceId,
            DestinationTable = DestinationTable,
            Mode = Mode,
            TransformationScript = TransformationScript
        };
    }
}
