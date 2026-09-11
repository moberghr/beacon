using System;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Beacon.Core.Data;

public static class BeaconSchema
{
    private static readonly Regex ValidIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

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
