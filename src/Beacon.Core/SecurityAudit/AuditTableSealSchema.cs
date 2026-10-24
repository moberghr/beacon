using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// Keeps the sealed audit tables (<see cref="AuditTableStores"/>) working while the database lacks their seal columns: the
/// Beacon schema is not migrated when the host starts, so a version that seals rows can run against a schema that has no
/// place for the seal until the host applies the migration <c>AddAuditTableSeal</c>. Whether a table lacks them is read
/// from the database's catalog (<see cref="IAuditTableSealProbe"/>, a read that never fails a transaction it joins) the
/// first time it matters and kept: columns found are kept for good, columns missing are read again after
/// <see cref="RecheckMissingAfter"/>. While they are missing, one Warning per table is logged, rows are written without the
/// seal (<c>SecurityAuditInterceptor</c>, through <see cref="InsertWithoutSeal"/>), the verification passes over the table
/// and the content expiry clears it as an unsealed store. A table whose state cannot be read is taken as migrated.
/// </summary>
internal sealed class AuditTableSealSchema(IAuditTableSealProbe probe, TimeProvider timeProvider, ILoggerFactory loggerFactory)
{
    /// <summary>The columns the seal adds to a sealed audit table's rows.</summary>
    internal static readonly IReadOnlyList<string> SealProperties =
    [
        nameof(ISealedAuditRow.AuditGuid),
        nameof(ISealedAuditRow.SealVersion),
        nameof(ISealedAuditRow.HmacKeyId),
        nameof(ISealedAuditRow.Hmac)
    ];

    /// <summary>How long a table found without its seal columns is taken as such before the catalog is read again.</summary>
    internal static readonly TimeSpan RecheckMissingAfter = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Type, DateTimeOffset?> _missingSince = new();
    private readonly ConcurrentDictionary<Type, bool> _warned = new();
    private readonly ILogger _logger = loggerFactory.CreateLogger<AuditTableSealSchema>();

    /// <summary>True while the table of <paramref name="entity"/> is known to lack its seal columns.</summary>
    public bool SealColumnsMissing(DbContext context, Type entity)
    {
        if (Known(entity) is { } known)
        {
            return known;
        }

        var entityType = context.Model.FindEntityType(entity);

        return entityType != null && Record(entity, probe.ColumnsMissing(context, entityType));
    }

    /// <inheritdoc cref="SealColumnsMissing"/>
    public async Task<bool> SealColumnsMissingAsync(DbContext context, Type entity, CancellationToken cancellationToken)
    {
        if (Known(entity) is { } known)
        {
            return known;
        }

        var entityType = context.Model.FindEntityType(entity);

        return entityType != null && Record(entity, await probe.ColumnsMissingAsync(context, entityType, cancellationToken));
    }

    /// <summary>
    /// The parameterised insert of <paramref name="row"/> without its seal columns, with the table, column names and
    /// parameter types of the context's own model, so it holds on either provider and under its naming convention.
    /// </summary>
    internal static (string Sql, object[] Parameters) InsertWithoutSeal(DbContext context, object row)
    {
        var entityType = context.Model.FindEntityType(row.GetType())
            ?? throw new InvalidOperationException("The sealed audit entity is not in the model.");
        var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        var generation = context.GetService<ISqlGenerationHelper>();
        var key = entityType.FindPrimaryKey()!.Properties.Single();
        using var command = context.Database.GetDbConnection().CreateCommand();
        var columns = new List<string>();
        var names = new List<string>();
        var parameters = new List<object>();
        foreach (var property in entityType.GetProperties())
        {
            if (property == key || SealProperties.Contains(property.Name))
            {
                continue;
            }

            var value = property.GetGetter().GetClrValue(row);
            var mapping = property.GetRelationalTypeMapping();
            var name = generation.GenerateParameterName("p" + parameters.Count.ToString(CultureInfo.InvariantCulture));
            parameters.Add(mapping.CreateParameter(command, name, mapping.Converter?.ConvertToProvider(value) ?? value, property.IsNullable));
            columns.Add(generation.DelimitIdentifier(property.GetColumnName(table)!));
            names.Add(name);
        }

        var sql = new StringBuilder()
            .Append("INSERT INTO ")
            .Append(generation.DelimitIdentifier(table.Name, table.Schema))
            .Append(" (")
            .AppendJoin(", ", columns)
            .Append(") VALUES (")
            .AppendJoin(", ", names)
            .Append(')');

        return (sql.ToString(), parameters.ToArray());
    }

    private bool? Known(Type entity)
    {
        if (!_missingSince.TryGetValue(entity, out var since))
        {
            return null;
        }

        if (since == null)
        {
            return false;
        }

        return timeProvider.GetUtcNow() - since.Value < RecheckMissingAfter ? true : null;
    }

    private bool Record(Type entity, bool? missing)
    {
        if (missing == null)
        {
            return false;
        }

        if (!missing.Value)
        {
            _missingSince[entity] = null;

            return false;
        }

        _missingSince[entity] = timeProvider.GetUtcNow();
        if (_warned.TryAdd(entity, true))
        {
            _logger.LogWarning(
                "The {Table} table has no seal columns: its rows are written without their seal and not verified until the Beacon migrations are applied (migration AddAuditTableSeal)",
                entity.Name);
        }

        return true;
    }
}

/// <summary>Reads from the database's catalog whether a sealed audit table lacks its seal columns.</summary>
internal interface IAuditTableSealProbe
{
    /// <summary>True when the table is there without its seal columns, false when they are there, null when it cannot tell.</summary>
    bool? ColumnsMissing(DbContext context, IEntityType entity);

    /// <inheritdoc cref="ColumnsMissing"/>
    Task<bool?> ColumnsMissingAsync(DbContext context, IEntityType entity, CancellationToken cancellationToken);
}

/// <summary>
/// Reads <c>INFORMATION_SCHEMA</c> (on PostgreSQL and SQL Server alike) on the context's own connection and transaction:
/// a catalog read that cannot fail on a missing column, so it never aborts the transaction a save runs in. Any other
/// failure answers that it cannot tell.
/// </summary>
internal sealed class InformationSchemaSealProbe : IAuditTableSealProbe
{
    private const string TablesSql =
        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table";

    private const string ColumnsSql =
        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table AND COLUMN_NAME IN (@c0, @c1, @c2, @c3)";

    public bool? ColumnsMissing(DbContext context, IEntityType entity)
    {
        try
        {
            context.Database.OpenConnection();
            try
            {
                using var tablesCommand = Count(context, entity, TablesSql);
                if (Convert.ToInt64(tablesCommand.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                {
                    return null;
                }

                using var columnsCommand = Count(context, entity, ColumnsSql);
                var columns = columnsCommand.ExecuteScalar();

                return Convert.ToInt64(columns, CultureInfo.InvariantCulture) < AuditTableSealSchema.SealProperties.Count;
            }
            finally
            {
                context.Database.CloseConnection();
            }
        }
        catch (DbException)
        {
            return null;
        }
    }

    public async Task<bool?> ColumnsMissingAsync(DbContext context, IEntityType entity, CancellationToken cancellationToken)
    {
        try
        {
            await context.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var tablesCommand = Count(context, entity, TablesSql);
                var tables = await tablesCommand.ExecuteScalarAsync(cancellationToken);
                if (Convert.ToInt64(tables, CultureInfo.InvariantCulture) == 0)
                {
                    return null;
                }

                await using var columnsCommand = Count(context, entity, ColumnsSql);
                var columns = await columnsCommand.ExecuteScalarAsync(cancellationToken);

                return Convert.ToInt64(columns, CultureInfo.InvariantCulture) < AuditTableSealSchema.SealProperties.Count;
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }
        }
        catch (DbException)
        {
            return null;
        }
    }

    // The catalog count for the entity's table (and its seal columns), as parameters only.
    private static DbCommand Count(DbContext context, IEntityType entity, string sql)
    {
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        Add(command, "@schema", table.Schema ?? string.Empty);
        Add(command, "@table", table.Name);
        if (sql == ColumnsSql)
        {
            for (var i = 0; i < AuditTableSealSchema.SealProperties.Count; i++)
            {
                Add(command, "@c" + i.ToString(CultureInfo.InvariantCulture), entity.FindProperty(AuditTableSealSchema.SealProperties[i])!.GetColumnName(table)!);
            }
        }

        return command;
    }

    private static void Add(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
