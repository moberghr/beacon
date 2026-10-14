using Beacon.Core.Authorization;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;

namespace Beacon.Core.Handlers.DataSources;

/// <summary>
/// The stored sample values a metadata response carries (<c>GET /data-sources/{id}/metadata</c>, the refresh): an Admin
/// gets them under the guardrails that apply now — the strictest of the live projects that link the data source, through
/// <see cref="StoredSampleScreen"/> — and every other caller gets none. Grounding reads stored samples itself; the
/// response only serves an Admin checking what was collected. The cached snapshot is left as it is.
/// </summary>
internal sealed class MetadataSampleView(
    IBeaconPermissionAccessor permissionAccessor,
    ISharedSourceSettings sharedSourceSettings,
    IQueryGuardrailService guardrail)
{
    public async Task<DatabaseMetadataSnapshot> ApplyAsync(DatabaseMetadataSnapshot snapshot, CancellationToken cancellationToken)
    {
        var permissions = await permissionAccessor.GetCurrentAsync(cancellationToken);
        var settings = permissions.IsAdmin
            ? await sharedSourceSettings.GetDataSourceSettingsAsync(snapshot.DataSourceId, cancellationToken)
            : null;

        return snapshot with
        {
            Tables = snapshot.Tables
                .Select(x => x with { Columns = WithSamples(x.Columns, settings) })
                .ToList()
        };
    }

    private List<ColumnMetadataDto> WithSamples(IReadOnlyList<ColumnMetadataDto> columns, McpSettingsData? settings)
    {
        return columns
            .Select(x => WithSamples(x, settings))
            .ToList();
    }

    // No settings: the caller is no Admin and sees no samples.
    private ColumnMetadataDto WithSamples(ColumnMetadataDto column, McpSettingsData? settings)
    {
        var shown = settings == null
            ? null
            : StoredSampleScreen.Screen(guardrail, column.ColumnName, column.SampleValues, settings);

        return shown?.Count == column.SampleValues?.Count
            ? column
            : column with { SampleValues = shown, SampleValuesComplete = false };
    }
}
