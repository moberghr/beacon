using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Update;
using Beacon.Core.Data;

namespace Beacon.Core.SqlServer.Data;

/// <summary>
/// Places Beacon's migration operations in the <c>beacon</c> schema: operations carrying the literal <c>"beacon"</c> or
/// no schema (null) are retargeted to it, so every committed migration stays byte-for-byte unchanged (CLAUDE.md
/// §0.1/§5.9). The retargeting itself lives in <see cref="MigrationOperationSchemaRetargeter"/> (Beacon.Core), shared
/// with the PostgreSQL provider; any other schema literal fails migration generation.
/// </summary>
internal sealed class SchemaAwareMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    ICommandBatchPreparer commandBatchPreparer)
    : SqlServerMigrationsSqlGenerator(dependencies, commandBatchPreparer)
{
    protected override void Generate(MigrationOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        MigrationOperationSchemaRetargeter.Retarget(operation, BeaconDatabaseConfiguration.DefaultSchema);

        base.Generate(operation, model, builder);
    }
}
