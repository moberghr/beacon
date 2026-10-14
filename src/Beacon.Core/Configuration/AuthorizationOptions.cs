namespace Beacon.Core;

/// <summary>
/// Configuration options for authorization.
/// </summary>
public class AuthorizationOptions
{
    /// <summary>
    /// Enable authorization checks. Default: false (backward compatible)
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Authorization provider type. If null, uses DefaultAuthorizationProvider.
    /// </summary>
    public Type? ProviderType { get; set; }

    /// <summary>
    /// Enable resource-level authorization (requires provider support).
    /// Default: false (use global read/write only)
    /// </summary>
    [Obsolete("Never read. Project and resource access is decided by IProjectAccessEvaluator; removed in 5.0.")]
    public bool EnableResourceLevelAuthorization { get; set; } = false;
}
