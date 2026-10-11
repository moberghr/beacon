using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.McpSettings;

/// <summary>
/// The MCP safety switches only an Admin may loosen: read-only enforcement and PII detection, globally and per project.
/// A SecurityAdmin tightens them (turns them on, or makes a project inherit a global value that is on) and changes every
/// other setting. A refusal is a 403, logged with ids only.
/// </summary>
internal static class McpSafetySettings
{
    public const string AdminOnlyMessage = "Only an Admin can turn off read-only enforcement or PII detection.";

    /// <summary>A global switch that was on and is turned off.</summary>
    public static bool Loosens(bool before, bool after)
    {
        return before && !after;
    }

    /// <summary>
    /// A project override (null inherits the global value) that turns the switch off, or pins it off where it inherited
    /// before; or one that stops pinning it on, which loosens it while the global value is off (null: decided by the
    /// global value, which the caller reads only then).
    /// </summary>
    public static bool? Loosens(bool? before, bool? after)
    {
        if (after == false && before != false)
        {
            return true;
        }

        return before == true && after == null ? null : false;
    }

    /// <summary>Refuses a caller who is not an Admin a change that loosens one of the switches.</summary>
    public static void DemandAdmin(BeaconPermissionSet permissions, BeaconActor actor, ILogger logger, int? projectId)
    {
        if (permissions.IsAdmin)
        {
            return;
        }

        throw AccessRefusal.Of(logger, actor, "MCP safety settings", projectId, AdminOnlyMessage);
    }
}
