using System;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Beacon.Core.Data;

/// <summary>
/// Retargets migration operations carrying the design-time literal schema ("beacon") or no
/// schema (null) to the schema configured via <c>UseBeaconSchema</c>, so every committed
/// migration stays byte-for-byte unchanged (CLAUDE.md §0.1/§5.9) while the SQL it generates
/// still lands in the schema the running host is configured for. Shared by the SQL Server and
/// PostgreSQL <c>SchemaAwareMigrationsSqlGenerator</c> subclasses: every
/// <see cref="MigrationOperation"/> subtype referenced here lives in
/// Microsoft.EntityFrameworkCore.Relational, which Beacon.Core already references, so this type
/// can live in Core without Core taking a ProjectReference on either provider project (§2.4).
///
/// CAVEAT (spec risk R4): only the literal "beacon" and null are retargeted automatically. A
/// migration scaffolded against a host already configured for a non-default schema bakes that
/// host's own literal (e.g. "tenant_a") into the migration file. If that literal does not match
/// the schema configured for the host running the migration, this throws at migration-generation
/// time rather than silently emitting DDL against the wrong schema. Scaffold new migrations
/// against a host configured for the default schema to avoid this.
///
/// NOT COVERED: the nested <c>AlterTableOperation.OldTable</c> / <c>AlterColumnOperation.OldColumn</c>
/// snapshots. Those are read by SqlServerMigrationsSqlGenerator only for temporal-table history
/// schemas, and this codebase uses no temporal tables (no <c>IsTemporal()</c> anywhere in the
/// model), so they are unreachable today. If temporal tables are ever adopted, add cases for
/// them — they are nested objects rather than top-level operations, so the schema-bearing
/// safety net in the default branch below will not catch the omission.
/// </summary>
public static class MigrationOperationSchemaRetargeter
{
    private const string DesignTimeSchema = "beacon";

    public static void Retarget(MigrationOperation operation, string? configuredSchema)
    {
        if (string.IsNullOrEmpty(configuredSchema))
        {
            return;
        }

        RetargetSchema(operation, configuredSchema);
    }

    private static void RetargetSchema(MigrationOperation operation, string configuredSchema)
    {
        switch (operation)
        {
            case TableOperation tableOperation:
                tableOperation.Schema = RetargetValue(tableOperation.Schema, configuredSchema, operation);
                break;
            case ColumnOperation columnOperation:
                columnOperation.Schema = RetargetValue(columnOperation.Schema, configuredSchema, operation);
                break;
            case CreateSequenceOperation createSequenceOperation:
                createSequenceOperation.Schema = RetargetValue(createSequenceOperation.Schema, configuredSchema, operation);
                break;
            case AlterSequenceOperation alterSequenceOperation:
                alterSequenceOperation.Schema = RetargetValue(alterSequenceOperation.Schema, configuredSchema, operation);
                break;
            case DropSequenceOperation dropSequenceOperation:
                dropSequenceOperation.Schema = RetargetValue(dropSequenceOperation.Schema, configuredSchema, operation);
                break;
            case DropColumnOperation dropColumnOperation:
                dropColumnOperation.Schema = RetargetValue(dropColumnOperation.Schema, configuredSchema, operation);
                break;
            case RenameColumnOperation renameColumnOperation:
                renameColumnOperation.Schema = RetargetValue(renameColumnOperation.Schema, configuredSchema, operation);
                break;
            case AddForeignKeyOperation addForeignKeyOperation:
                addForeignKeyOperation.Schema = RetargetValue(addForeignKeyOperation.Schema, configuredSchema, operation);
                addForeignKeyOperation.PrincipalSchema = RetargetValue(addForeignKeyOperation.PrincipalSchema, configuredSchema, operation);
                break;
            case DropForeignKeyOperation dropForeignKeyOperation:
                dropForeignKeyOperation.Schema = RetargetValue(dropForeignKeyOperation.Schema, configuredSchema, operation);
                break;
            case AddPrimaryKeyOperation addPrimaryKeyOperation:
                addPrimaryKeyOperation.Schema = RetargetValue(addPrimaryKeyOperation.Schema, configuredSchema, operation);
                break;
            case DropPrimaryKeyOperation dropPrimaryKeyOperation:
                dropPrimaryKeyOperation.Schema = RetargetValue(dropPrimaryKeyOperation.Schema, configuredSchema, operation);
                break;
            case AddUniqueConstraintOperation addUniqueConstraintOperation:
                addUniqueConstraintOperation.Schema = RetargetValue(addUniqueConstraintOperation.Schema, configuredSchema, operation);
                break;
            case DropUniqueConstraintOperation dropUniqueConstraintOperation:
                dropUniqueConstraintOperation.Schema = RetargetValue(dropUniqueConstraintOperation.Schema, configuredSchema, operation);
                break;
            case AddCheckConstraintOperation addCheckConstraintOperation:
                addCheckConstraintOperation.Schema = RetargetValue(addCheckConstraintOperation.Schema, configuredSchema, operation);
                break;
            case DropCheckConstraintOperation dropCheckConstraintOperation:
                dropCheckConstraintOperation.Schema = RetargetValue(dropCheckConstraintOperation.Schema, configuredSchema, operation);
                break;
            case CreateIndexOperation createIndexOperation:
                createIndexOperation.Schema = RetargetValue(createIndexOperation.Schema, configuredSchema, operation);
                break;
            case DropIndexOperation dropIndexOperation:
                dropIndexOperation.Schema = RetargetValue(dropIndexOperation.Schema, configuredSchema, operation);
                break;
            case RenameIndexOperation renameIndexOperation:
                renameIndexOperation.Schema = RetargetValue(renameIndexOperation.Schema, configuredSchema, operation);
                break;
            case DropTableOperation dropTableOperation:
                dropTableOperation.Schema = RetargetValue(dropTableOperation.Schema, configuredSchema, operation);
                break;
            case RenameTableOperation renameTableOperation:
                renameTableOperation.Schema = RetargetValue(renameTableOperation.Schema, configuredSchema, operation);
                renameTableOperation.NewSchema = RetargetValue(renameTableOperation.NewSchema, configuredSchema, operation);
                break;
            case InsertDataOperation insertDataOperation:
                insertDataOperation.Schema = RetargetValue(insertDataOperation.Schema, configuredSchema, operation);
                break;
            case DeleteDataOperation deleteDataOperation:
                deleteDataOperation.Schema = RetargetValue(deleteDataOperation.Schema, configuredSchema, operation);
                break;
            case UpdateDataOperation updateDataOperation:
                updateDataOperation.Schema = RetargetValue(updateDataOperation.Schema, configuredSchema, operation);
                break;
            case RenameSequenceOperation renameSequenceOperation:
                renameSequenceOperation.Schema = RetargetValue(renameSequenceOperation.Schema, configuredSchema, operation);
                renameSequenceOperation.NewSchema = RetargetValue(renameSequenceOperation.NewSchema, configuredSchema, operation);
                break;
            case RestartSequenceOperation restartSequenceOperation:
                restartSequenceOperation.Schema = RetargetValue(restartSequenceOperation.Schema, configuredSchema, operation);
                break;
            case EnsureSchemaOperation ensureSchemaOperation:
                ensureSchemaOperation.Name = RetargetValue(ensureSchemaOperation.Name, configuredSchema, operation);
                break;
            case DropSchemaOperation dropSchemaOperation:
                dropSchemaOperation.Name = RetargetValue(dropSchemaOperation.Name, configuredSchema, operation);
                break;
            default:
                ThrowIfSchemaBearing(operation);
                break;
        }

        if (operation is CreateTableOperation createTableOperation)
        {
            RetargetNestedOperations(createTableOperation, configuredSchema);
        }
    }

    private static void RetargetNestedOperations(CreateTableOperation createTableOperation, string configuredSchema)
    {
        foreach (var column in createTableOperation.Columns)
        {
            RetargetSchema(column, configuredSchema);
        }

        foreach (var foreignKey in createTableOperation.ForeignKeys)
        {
            RetargetSchema(foreignKey, configuredSchema);
        }

        if (createTableOperation.PrimaryKey != null)
        {
            RetargetSchema(createTableOperation.PrimaryKey, configuredSchema);
        }

        foreach (var uniqueConstraint in createTableOperation.UniqueConstraints)
        {
            RetargetSchema(uniqueConstraint, configuredSchema);
        }

        foreach (var checkConstraint in createTableOperation.CheckConstraints)
        {
            RetargetSchema(checkConstraint, configuredSchema);
        }
    }

    // Also used for the non-nullable EnsureSchemaOperation.Name / DropSchemaOperation.Name
    // properties (a string value flows in, a string value flows out) — those two operations
    // carry their schema name in "Name" rather than "Schema".
    private static string RetargetValue(string? value, string configuredSchema, MigrationOperation operation)
    {
        if (value == null || value == DesignTimeSchema)
        {
            return configuredSchema;
        }

        if (value == configuredSchema)
        {
            return value;
        }

        throw new InvalidOperationException(
            $"{operation.GetType().Name} carries the schema literal '{value}', which is neither " +
            $"\"{DesignTimeSchema}\" nor the configured schema '{configuredSchema}'. Only \"{DesignTimeSchema}\" " +
            "and null are retargeted automatically (spec risk R4) — scaffold new migrations against a host " +
            "configured for the default schema so this literal does not get baked into a committed migration.");
    }

    // Safety net for a future EF Core release that introduces a new schema-bearing
    // MigrationOperation subtype: rather than silently letting it pass through unretargeted,
    // fail loudly so the omission gets a case added here. A non-schema-bearing operation
    // hitting this branch (the common case) stays silent, as it always has.
    private static void ThrowIfSchemaBearing(MigrationOperation operation)
    {
        if (operation.GetType().GetProperty("Schema", typeof(string)) == null)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Migration operation type {operation.GetType().Name} declares a Schema property but has no " +
            $"explicit case in {nameof(MigrationOperationSchemaRetargeter)}. Add one before it can run " +
            "safely against a configured schema.");
    }
}
