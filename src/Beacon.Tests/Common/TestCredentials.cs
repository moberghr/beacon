namespace Beacon.Tests.Common;

/// <summary>
/// Credential-shaped values for the tests of the screens and logs that must withhold them. Assembled from parts so the
/// source holds no literal a secret scanner reports; none of them is a credential any service issued.
/// </summary>
internal static class TestCredentials
{
    /// <summary>A value in the shape of a live payment-provider secret key: a well-known prefix and 24 characters.</summary>
    public const string ProviderSecretKey = "sk_" + "live_" + "BeaconTestOnlyNotAKey000";

    /// <summary>A value in the shape of an AWS access key id (a well-known prefix and 16 characters).</summary>
    public const string AwsAccessKeyId = "AK" + "IA" + "BEACONTESTONLY00";

    /// <summary>A value in the shape of an AWS secret access key (40 characters).</summary>
    public const string AwsSecretAccessKey = "BeaconTestOnly" + "NotARealAwsSecretKey0000000";

    /// <summary>A value in the shape of an AWS STS session token.</summary>
    public const string AwsSessionToken = "BeaconTestOnly" + "SessionTokenNotReal";
}
