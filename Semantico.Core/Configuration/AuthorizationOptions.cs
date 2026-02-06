namespace Semantico.Core;

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
    public bool EnableResourceLevelAuthorization { get; set; } = false;
}
