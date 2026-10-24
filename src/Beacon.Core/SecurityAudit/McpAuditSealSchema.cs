using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// Keeps the MCP audit written while the database lacks the seal columns (<c>AuditGuid</c>, <c>SealVersion</c>,
/// <c>HmacKeyId</c>, <c>Hmac</c>): the Beacon schema is not migrated when the host starts, so a version that seals rows
/// can run against a schema that has no place for the seal until the host applies the migration. Then an audit row is
/// never lost: <see cref="SealColumnsMissingAsync"/> tells that the table is there and its seal columns are not, and
/// <see cref="WriteWithoutSealAsync"/> writes the row and its statements without them, in one transaction. Such a row is
/// reported as unsealed by the verification (written before sealing started). Checked at startup (an Error while the
/// columns are missing) and after an audit write fails.
/// </summary>
public interface IMcpAuditSealSchema
{
    /// <summary>
    /// True when the MCP audit table is there and its seal columns are not; false when they are; null when it cannot tell
    /// (the table cannot be read at all).
    /// </summary>
    Task<bool?> SealColumnsMissingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="row"/> and its statements without the seal columns, with the values it holds (after the save
    /// interceptors ran on it, content retention included), and returns the row's id.
    /// </summary>
    Task<int> WriteWithoutSealAsync(McpAuditLog row, CancellationToken cancellationToken);
}

internal sealed class McpAuditSealSchema(IDbContextFactory<BeaconContext> contextFactory) : IMcpAuditSealSchema
{
    /// <summary>The columns the seal adds to the MCP audit rows.</summary>
    internal static readonly IReadOnlySet<string> SealProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(McpAuditLog.AuditGuid),
        nameof(McpAuditLog.SealVersion),
        nameof(McpAuditLog.HmacKeyId),
        nameof(McpAuditLog.Hmac)
    };

    private const string PostgreSqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string SqlServerProvider = "Microsoft.EntityFrameworkCore.SqlServer";
    private const string IdColumnAlias = "Value";

    public async Task<bool?> SealColumnsMissingAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            await context.McpAuditLogs
                .AsNoTracking()
                .Where(x => x.Id == 0)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
        }
        catch (DbException)
        {
            return null;
        }

        try
        {
            await context.McpAuditLogs
                .AsNoTracking()
                .Where(x => x.Id == 0)
                .Select(x =>
                    new
                    {
                        x.AuditGuid,
                        x.SealVersion,
                        x.HmacKeyId,
                        x.Hmac
                    })
                .ToListAsync(cancellationToken);

            return false;
        }
        catch (DbException)
        {
            return true;
        }
    }

    public async Task<int> WriteWithoutSealAsync(McpAuditLog row, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var (rowSql, rowParameters) = Insert(context, row, null);
            var ids = await context.Database
                .SqlQueryRaw<int>(rowSql, rowParameters)
                .ToListAsync(cancellationToken);
            var id = ids.Single();
            foreach (var statement in row.Statements)
            {
                var (statementSql, statementParameters) = Insert(context, statement, id);
                await context.Database.ExecuteSqlRawAsync(statementSql, statementParameters, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            return id;
        });
    }

    /// <summary>
    /// The parameterised insert of <paramref name="entity"/> (a row without its seal columns, returning its id; or a
    /// statement of the row <paramref name="auditLogId"/>), with the table, column names and parameter types of the
    /// context's own model, so it holds on either provider and under its naming convention.
    /// </summary>
    internal static (string Sql, object[] Parameters) Insert(DbContext context, object entity, int? auditLogId)
    {
        var entityType = context.Model.FindEntityType(entity.GetType())
            ?? throw new InvalidOperationException("The MCP audit entity is not in the model.");
        var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        var generation = context.GetService<ISqlGenerationHelper>();
        var key = entityType.FindPrimaryKey()!.Properties.Single();
        var properties = entityType.GetProperties()
            .Where(x => x != key)
            .Where(x => entity is not McpAuditLog || !SealProperties.Contains(x.Name))
            .ToList();

        using var command = context.Database.GetDbConnection().CreateCommand();
        var columns = new List<string>();
        var names = new List<string>();
        var parameters = new List<object>();
        foreach (var property in properties)
        {
            var value = auditLogId != null && property.Name == nameof(McpAuditStatement.McpAuditLogId)
                ? auditLogId.Value
                : property.GetGetter().GetClrValue(entity);
            var mapping = property.GetRelationalTypeMapping();
            var name = generation.GenerateParameterName("p" + parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            parameters.Add(mapping.CreateParameter(command, name, mapping.Converter?.ConvertToProvider(value) ?? value, property.IsNullable));
            columns.Add(generation.DelimitIdentifier(property.GetColumnName(table)!));
            names.Add(name);
        }

        var tableName = generation.DelimitIdentifier(table.Name, table.Schema);
        var idColumn = generation.DelimitIdentifier(key.GetColumnName(table)!);
        var alias = generation.DelimitIdentifier(IdColumnAlias);
        var sql = new StringBuilder()
            .Append("INSERT INTO ")
            .Append(tableName)
            .Append(" (")
            .AppendJoin(", ", columns)
            .Append(')');
        if (entity is McpAuditLog && context.Database.ProviderName == SqlServerProvider)
        {
            sql.Append(" OUTPUT INSERTED.").Append(idColumn).Append(" AS ").Append(alias);
        }

        sql.Append(" VALUES (")
            .AppendJoin(", ", names)
            .Append(')');
        if (entity is McpAuditLog && context.Database.ProviderName == PostgreSqlProvider)
        {
            sql.Append(" RETURNING ").Append(idColumn).Append(" AS ").Append(alias);
        }

        if (entity is McpAuditLog && context.Database.ProviderName is not (SqlServerProvider or PostgreSqlProvider))
        {
            throw new NotSupportedException("An MCP audit row is written without its seal on PostgreSQL and SQL Server only.");
        }

        return (sql.ToString(), parameters.ToArray());
    }
}
