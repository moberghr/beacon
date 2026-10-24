using System;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Beacon.Core.Data;

public static class BeaconSchema
{
    private static readonly Regex ValidIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    public static string? Resolve(DbContext context)
    {
        return context.GetService<IDbContextOptions>()
            .FindExtension<BeaconSchemaOptionsExtension>()
            ?.Schema;
    }

    public static string ValidateIdentifier(string schema)
    {
        if (!ValidIdentifier.IsMatch(schema))
        {
            throw new InvalidOperationException($"'{schema}' is not a valid schema identifier.");
        }

        return schema;
    }

    /// <summary>
    /// Creates the schema when <paramref name="createSchema"/> is set and applies Beacon's pending migrations through
    /// <paramref name="context"/> — what <c>UseBeacon</c> does, for a context a migration process built without composing
    /// Beacon (<c>BeaconPostgreSqlMigrator</c>, <c>BeaconSqlServerMigrator</c>). Idempotent.
    /// </summary>
    public static async Task MigrateAsync(DbContext context, bool createSchema, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Validated before any DDL, as UseBeacon does.
        var schema = ValidateIdentifier(Resolve(context) ?? "beacon");
        if (createSchema)
        {
#pragma warning disable EF1002 // identifier validated above; DDL cannot take parameters
            await context.Database.ExecuteSqlRawAsync(CreateSchemaStatement(context.Database.ProviderName, schema), cancellationToken);
#pragma warning restore EF1002
        }

        await context.Database.MigrateAsync(cancellationToken);
    }

    internal static string CreateSchemaStatement(string? providerName, string schema)
    {
        ValidateIdentifier(schema);

        if (providerName != null && providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            return $"CREATE SCHEMA IF NOT EXISTS \"{schema}\";";
        }

        return $"IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{schema}') EXEC('CREATE SCHEMA [{schema}]');";
    }
}
