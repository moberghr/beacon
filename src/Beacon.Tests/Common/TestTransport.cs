using Beacon.Connector.Api;
using Beacon.Connector.AzureSynapse;
using Beacon.Connector.BigQuery;
using Beacon.Connector.CloudWatch;
using Beacon.Connector.Databricks;
using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.Snowflake;
using Beacon.Connector.SqlServer;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Beacon.Tests.Common;

/// <summary>
/// The data-source transport policy with every connector's transport and option inspector, as a host registering them
/// all has it.
/// </summary>
internal static class TestTransport
{
    public static List<IDataSourceTransportInspector> Inspectors()
    {
        return
        [
            new PostgreSqlTransportInspector(),
            new SqlServerTransportInspector(),
            new AzureSynapseTransportInspector(),
            new MySqlTransportInspector(),
            new SnowflakeTransportInspector(),
            new ApiTransportInspector(),
            new DatabricksTransportInspector(),
            new BigQueryTransportInspector(),
            new CloudWatchTransportInspector()
        ];
    }

    public static List<IDataSourceConnectionOptionInspector> OptionInspectors()
    {
        return
        [
            new PostgreSqlConnectionOptionInspector(),
            new SqlServerConnectionOptionInspector(),
            new AzureSynapseConnectionOptionInspector(),
            new MySqlConnectionOptionInspector(),
            new SnowflakeConnectionOptionInspector(),
            new ApiConnectionOptionInspector(),
            new DatabricksConnectionOptionInspector(),
            new BigQueryConnectionOptionInspector(),
            new CloudWatchConnectionOptionInspector()
        ];
    }

    public static IDataSourceTransportPolicy Policy(
        bool allowInsecure = false,
        bool enforceOnExisting = false,
        ILogger<DataSourceTransportPolicy>? logger = null,
        IReadOnlyList<string>? allowedHosts = null,
        bool allowHostCredentials = false)
    {
        return new DataSourceTransportPolicy(
            Inspectors(),
            OptionInspectors(),
            Options.Create(new DataSourceConnectionOptions { AllowedHosts = allowedHosts?.ToList() ?? [], AllowHostCredentials = allowHostCredentials }),
            Options.Create(new DataSourceTransportOptions { AllowInsecure = allowInsecure, EnforceOnExisting = enforceOnExisting }),
            logger ?? NullLogger<DataSourceTransportPolicy>.Instance);
    }

    /// <summary>The connection resolver over <paramref name="encryption"/>, with no host connection strings configured.</summary>
    public static IDataSourceConnectionResolver Resolver(IEncryptionService encryption, IDataSourceTransportPolicy? policy = null)
    {
        return new DataSourceConnectionResolver(encryption, new ConfigurationBuilder().Build(), policy ?? Policy());
    }
}
