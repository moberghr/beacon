using System.Text.Json;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;

namespace Beacon.Core.Services;

/// <summary>
/// The steps a migration job's <c>QueryText</c> runs, read one way for running the job and for deciding who may create or
/// run it: a JSON array of steps, each naming its own data source; anything else is one step of plain SQL that names no
/// data source.
/// </summary>
internal static class MigrationQuerySteps
{
    public static List<QueryStepData> Parse(string? queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return [];
        }

        try
        {
            var stepObjects = JsonSerializer.Deserialize<List<JsonElement>>(queryText);

            return stepObjects?
                .Select((step, index) =>
                    new QueryStepData
                    {
                        StepId = 0, // Temporary step
                        StepOrder = step.TryGetProperty("StepOrder", out var stepOrder) ? stepOrder.GetInt32() : index + 1,
                        Name = step.TryGetProperty("Name", out var name) ? name.GetString() : $"Step {index + 1}",
                        Description = step.TryGetProperty("Description", out var desc) ? desc.GetString() : null,
                        SqlValue = step.TryGetProperty("SqlValue", out var sql) ? sql.GetString() ?? "" : "",
                        DataSourceId = step.TryGetProperty("DataSourceId", out var dataSourceId) ? dataSourceId.GetInt32() : 0,
                        DataSourceName = step.TryGetProperty("DataSourceName", out var dataSourceName) ? dataSourceName.GetString() : "",
                        DatabaseEngineType = DatabaseEngineType.PostgreSQL, // Default
                        Parameters = []
                    })
                .ToList() ?? [];
        }
        catch
        {
            // Fallback: treat as plain SQL (old format)
            return
            [
                new QueryStepData
                {
                    StepId = 0,
                    StepOrder = 1,
                    Name = "Migration Query",
                    Description = "Data extraction query",
                    SqlValue = queryText,
                    DataSourceId = 0, // Will need to be resolved
                    DataSourceName = "Unknown",
                    DatabaseEngineType = DatabaseEngineType.PostgreSQL,
                    Parameters = []
                }
            ];
        }
    }

    /// <summary>
    /// Every data source a job reads: the source it declares (<paramref name="declaredSourceId"/>) and the data source of
    /// each step its <paramref name="queryText"/> runs. A step naming no data source reads none.
    /// </summary>
    public static IReadOnlyList<int> SourceIds(int declaredSourceId, string? queryText)
    {
        return Parse(queryText)
            .Select(x => x.DataSourceId)
            .Where(x => x > 0)
            .Prepend(declaredSourceId)
            .Distinct()
            .ToList();
    }
}
