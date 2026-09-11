using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Update;
using Beacon.Core.Data;

namespace Beacon.Core.SqlServer.Data;

/// <summary>
/// Retargets migration operations carrying the design-time literal schema ("beacon") or no
/// schema (null) to the schema configured via <c>UseBeaconSchema</c>, so every committed
/// migration stays byte-for-byte unchanged (CLAUDE.md §0.1/§5.9) while the SQL it generates
/// still lands in the schema the running host is configured for. The retargeting logic itself
/// lives in <see cref="MigrationOperationSchemaRetargeter"/> (Beacon.Core), shared with the
/// PostgreSQL provider.
///
/// CAVEAT (spec risk R4, accepted): only the literal "beacon" and null are retargeted. A
/// migration scaffolded against a host already configured for a non-default schema bakes
/// that host's own literal (e.g. "tenant_a") into the migration file, and that literal will
/// NOT be retargeted here. Scaffold new migrations against a host configured for the
/// default schema to avoid this.
/// </summary>
internal sealed class SchemaAwareMigrationsSqlGenerator : SqlServerMigrationsSqlGenerator
{
    private readonly string? _configuredSchema;

    public SchemaAwareMigrationsSqlGenerator(
        MigrationsSqlGeneratorDependencies dependencies,
        ICommandBatchPreparer commandBatchPreparer)
        : base(dependencies, commandBatchPreparer)
    {
        _configuredSchema = BeaconSchema.Resolve(dependencies.CurrentContext.Context);
    }

    protected override void Generate(MigrationOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        MigrationOperationSchemaRetargeter.Retarget(operation, _configuredSchema);

        base.Generate(operation, model, builder);
    }
}
