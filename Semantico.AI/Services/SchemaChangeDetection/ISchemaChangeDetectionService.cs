namespace Semantico.AI.Services.SchemaChangeDetection;

public interface ISchemaChangeDetectionService
{
    Task<int> DetectChangesAsync(int dataSourceId, CancellationToken ct = default);
    Task TakeSnapshotAsync(int dataSourceId, CancellationToken ct = default);
}
