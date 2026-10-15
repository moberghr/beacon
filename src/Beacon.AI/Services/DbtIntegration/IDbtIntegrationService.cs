namespace Beacon.AI.Services.DbtIntegration;

public interface IDbtIntegrationService
{
    Task<DbtManifest> ParseManifestAsync(string manifestJson, CancellationToken ct = default);

    /// <summary>
    /// Imports a dbt manifest's models and sources as code references of <paramref name="projectId"/>, and their column
    /// descriptions into <paramref name="dataSourceId"/>'s metadata. The project must link the data source
    /// (<see cref="InvalidOperationException"/> otherwise), so imported references never land in a project that does not
    /// hold the source they describe.
    /// </summary>
    Task<int> ImportModelsAsync(int projectId, int dataSourceId, DbtManifest manifest, CancellationToken ct = default);
}

public record DbtManifest
{
    public List<DbtModel> Models { get; init; } = new();
    public List<DbtSource> Sources { get; init; } = new();
    public List<DbtTest> Tests { get; init; } = new();
}

public record DbtModel(string Name, string? Schema, string? Description, string? MaterializedAs, List<DbtColumn> Columns, List<string> DependsOn);
public record DbtSource(string Name, string? Schema, string? Description, List<DbtColumn> Columns);
public record DbtColumn(string Name, string? Description, List<string> Tests);
public record DbtTest(string Name, string? ModelName, string? ColumnName, string TestType);
