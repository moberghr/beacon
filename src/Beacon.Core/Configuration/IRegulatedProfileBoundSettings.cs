namespace Beacon.Core.Configuration;

/// <summary>
/// Settings of the regulated profile that a layer above Core binds into its own options (Beacon.Api's
/// <c>Beacon:Security</c>): the profile judges their bound value, whether the host set it in configuration or in code, in
/// place of the configuration key Core would read. Registered with <c>TryAddEnumerable</c>.
/// </summary>
internal interface IRegulatedProfileBoundSettings
{
    /// <summary>Whether the bound value of <paramref name="setting"/> meets the profile; null for a setting this layer does not bind.</summary>
    bool? Holds(string setting);
}

/// <summary>
/// Registered by the MCP gateway (<c>AddBeaconMcpGateway</c>): Beacon serves only its gateway part there, with none of the
/// changes the approval workflow governs (queries, migration jobs, data contracts, recipients, data sources, the LLM
/// connection, user roles), so the regulated profile does not require the workflow.
/// </summary>
internal sealed class McpGatewayDeployment;
