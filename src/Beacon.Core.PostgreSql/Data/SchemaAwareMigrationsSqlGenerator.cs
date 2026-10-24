using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;
using Beacon.Core.Data;

namespace Beacon.Core.PostgreSql.Data;

/// <summary>
/// Places Beacon's migration operations in the <c>beacon</c> schema: operations carrying no schema (null) or the literal
/// <c>"beacon"</c> are retargeted to it, so every committed migration stays byte-for-byte unchanged (CLAUDE.md §0.1/§5.9).
/// For PostgreSQL null is the common case: most committed migrations under this project's Data/Migrations/ carry no
/// schema argument at all (the model previously had no default schema), so retargeting null is what schema-qualifies
/// their SQL. The retargeting itself lives in <see cref="MigrationOperationSchemaRetargeter"/> (Beacon.Core), shared with
/// the SQL Server provider; any other schema literal fails migration generation.
/// </summary>
internal sealed class SchemaAwareMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    INpgsqlSingletonOptions npgsqlSingletonOptions)
    : NpgsqlMigrationsSqlGenerator(dependencies, npgsqlSingletonOptions)
{
    protected override void Generate(MigrationOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        MigrationOperationSchemaRetargeter.Retarget(operation, BeaconDatabaseConfiguration.DefaultSchema);

        base.Generate(operation, model, builder);
    }
}
