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
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Beacon.Tests.Common;

/// <summary>The data-source transport policy with every connector's inspector, as a host registering them all has it.</summary>
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

    public static IDataSourceTransportPolicy Policy(
        bool allowInsecure = false,
        bool enforceOnExisting = false,
        ILogger<DataSourceTransportPolicy>? logger = null)
    {
        return new DataSourceTransportPolicy(
            Inspectors(),
            Options.Create(new DataSourceTransportOptions { AllowInsecure = allowInsecure, EnforceOnExisting = enforceOnExisting }),
            logger ?? NullLogger<DataSourceTransportPolicy>.Instance);
    }
}
