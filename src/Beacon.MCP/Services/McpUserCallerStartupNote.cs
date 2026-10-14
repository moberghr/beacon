using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core;
using Beacon.Core.Configuration;

namespace Beacon.MCP.Services;

/// <summary>
/// Notes once at startup that Entra user callers are provisioned with the <c>Viewer</c> role
/// (<c>Beacon:Mcp:Callers:Users:DefaultRoleName</c>): a provisioned user reads the catalog, and the SQL tools need an
/// Editor role in the project, so a deployment that expects its users to run SQL sees why they are refused.
/// </summary>
internal sealed class McpUserCallerStartupNote(
    IOptions<McpCallerOptions> options,
    ILogger<McpUserCallerStartupNote> logger,
    BeaconConfiguration? configuration = null) : IStartupFilter
{
    // The built-in role names of Beacon user management.
    private const string ViewerRoleName = "Viewer";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        var users = options.Value.Users;
        if (users.Enabled
            && users.AutoProvision
            && (configuration?.UserManagement.Enabled ?? true)
            && string.Equals(users.DefaultRoleName?.Trim(), ViewerRoleName, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "MCP user callers are provisioned with the Viewer role (Beacon:Mcp:Callers:Users:DefaultRoleName): a provisioned user reads the project catalog, and the SQL tools (ask, query, dry_run, feedback) need an Editor role in the project.");
        }

        return next;
    }
}
