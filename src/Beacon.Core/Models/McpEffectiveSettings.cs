namespace Beacon.Core.Models;

/// <summary>
/// The resolved settings for one project (or the global row when no project applies) together with the fields
/// a deployment lock pinned and the fields a ceiling lowered, so the UI can hide the former and annotate the
/// latter. <see cref="Effective"/> is exactly what every MCP consumer reads.
/// </summary>
public sealed record McpEffectiveSettings(
    McpSettingsData Effective,
    IReadOnlySet<string> LockedFields,
    IReadOnlySet<string> ClampedFields);
