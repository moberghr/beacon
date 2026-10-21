namespace Beacon.Core.Mcp;

/// <summary>Options of <see cref="HostUserMcpCallerMapper"/>: which delegated Entra tokens may use MCP.</summary>
public sealed class HostUserMcpCallerOptions
{
    /// <summary>The only tenant whose tokens are accepted (<c>tid</c>). Required.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// The delegated scope a token must carry in <c>scp</c> (its short name, compared exactly): the scope the MCP client
    /// was consented for, not merely any scope of the application's registration. Default <c>mcp.access</c>.
    /// </summary>
    public string RequiredScope { get; set; } = "mcp.access";

    /// <summary>
    /// When not empty, the only client applications (<c>azp</c>, or <c>appid</c> in a v1 token) whose tokens are accepted —
    /// the MCP client's registration. Empty accepts any client the tenant consented.
    /// </summary>
    public List<string> AllowedClientIds { get; set; } = [];

    /// <summary>The host project every accepted caller reaches. Required.</summary>
    public string ProjectName { get; set; } = string.Empty;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(TenantId))
        {
            throw new InvalidOperationException("MCP host-user callers: the tenant id is required.");
        }

        if (string.IsNullOrWhiteSpace(RequiredScope))
        {
            throw new InvalidOperationException("MCP host-user callers: the required scope must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(ProjectName))
        {
            throw new InvalidOperationException("MCP host-user callers: the host project name is required.");
        }
    }
}
