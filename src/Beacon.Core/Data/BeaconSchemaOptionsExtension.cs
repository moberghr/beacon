using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Core.Data;

public sealed class BeaconSchemaOptionsExtension : IDbContextOptionsExtension
{
    private readonly ExtensionInfo _info;

    public BeaconSchemaOptionsExtension(string schema)
    {
        Schema = schema;
        _info = new ExtensionInfo(this);
    }

    public string Schema { get; }

    public DbContextOptionsExtensionInfo Info => _info;

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
        var historySchema = options.Extensions
            .OfType<RelationalOptionsExtension>()
            .FirstOrDefault()
            ?.MigrationsHistoryTableSchema;

        if (historySchema == null || historySchema == Schema)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Beacon schema is misconfigured: UseBeaconSchema is set to '{Schema}' but " +
            $"MigrationsHistoryTable is configured with schema '{historySchema}'. The history " +
            "table and the model-derived tables must resolve to the same schema — pass the same " +
            "schema value to both, or fix whichever one is stale.");
    }

    private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        public ExtensionInfo(BeaconSchemaOptionsExtension extension)
            : base(extension)
        {
        }

        private new BeaconSchemaOptionsExtension Extension => (BeaconSchemaOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider => false;

        public override string LogFragment => $"BeaconSchema={Extension.Schema} ";

        public override int GetServiceProviderHashCode()
        {
            return Extension.Schema.GetHashCode();
        }

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
        {
            return other is ExtensionInfo otherInfo
                && otherInfo.Extension.Schema == Extension.Schema;
        }

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            debugInfo["Beacon:Schema"] = Extension.Schema;
        }
    }
}

public static class BeaconSchemaDbContextOptionsBuilderExtensions
{
    public static DbContextOptionsBuilder UseBeaconSchema(this DbContextOptionsBuilder builder, string schema)
    {
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new BeaconSchemaOptionsExtension(schema));

        return builder;
    }
}
