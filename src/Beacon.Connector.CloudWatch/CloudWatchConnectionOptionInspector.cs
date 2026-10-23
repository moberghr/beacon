using System.Text.Json;
using System.Text.RegularExpressions;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Providers.CloudWatch;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.CloudWatch;

/// <summary>
/// A CloudWatch connection must name an AWS region (<c>Region</c>, such as <c>eu-central-1</c>) and carry its own access
/// key (<c>AccessKeyId</c> and <c>SecretAccessKey</c>): a <c>ProfileName</c> reads the Beacon host's AWS profiles, and no
/// key signs in with the host's default credentials (its IAM role). Read the way the provider reads its configuration.
/// The client reaches only the region's AWS endpoint, so the connection names no host.
/// </summary>
public sealed partial class CloudWatchConnectionOptionInspector : IDataSourceConnectionOptionInspector
{
    public DataSourceType DataSourceType => DataSourceType.CloudWatch;

    public DatabaseEngineType? DatabaseEngineType => null;

    public DataSourceConnectionOptionVerdict Inspect(string connectionData)
    {
        var config = JsonSerializer.Deserialize<CloudWatchConfiguration>(connectionData);
        if (config == null)
        {
            return DataSourceConnectionOptionVerdict.NotReadable();
        }

        if (string.IsNullOrEmpty(config.Region) || !RegionName().IsMatch(config.Region))
        {
            return DataSourceConnectionOptionVerdict.Refuse("Region", "Set Region to an AWS region name, such as eu-central-1", DataSourceConnectionOptionRisk.Unsafe, []);
        }

        if (!string.IsNullOrEmpty(config.AccessKeyId) && !string.IsNullOrEmpty(config.SecretAccessKey))
        {
            return DataSourceConnectionOptionVerdict.Accept([]);
        }

        return !string.IsNullOrEmpty(config.ProfileName)
            ? DataSourceConnectionOptionVerdict.Refuse("ProfileName", "Remove ProfileName and give AccessKeyId and SecretAccessKey", DataSourceConnectionOptionRisk.HostCredentials, [])
            : DataSourceConnectionOptionVerdict.Refuse("AccessKeyId", "Give AccessKeyId and SecretAccessKey", DataSourceConnectionOptionRisk.HostCredentials, []);
    }

    // An AWS region name in any partition (eu-central-1, us-gov-west-1, cn-north-1, us-isob-east-1, eusc-de-east-1):
    // lower-case labels the SDK writes into the endpoint's host name, ending in the region's number.
    [GeneratedRegex(@"^[a-z]{2,4}(-[a-z0-9]+)+-[0-9]{1,2}$")]
    private static partial Regex RegionName();
}
