using MediatR;
using Beacon.Core.Configuration;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.McpSettings;

internal sealed class GetMcpSettingsHandler(IMcpSettingsProvider settingsProvider)
    : IRequestHandler<GetMcpSettingsQuery, McpSettingsData>
{
    public async Task<McpSettingsData> Handle(GetMcpSettingsQuery request, CancellationToken cancellationToken)
    {
        var detail = await settingsProvider.GetEffectiveSettingsDetailAsync(0, cancellationToken);
        var result = new GetMcpSettingsResult
        {
            Stored = await settingsProvider.GetStoredSettingsAsync(cancellationToken),
            LockedFields = detail.LockedFields
                .Order(StringComparer.Ordinal)
                .ToArray(),
            ClampedFields = detail.ClampedFields
                .Order(StringComparer.Ordinal)
                .ToArray()
        };
        foreach (var field in McpSettingFields.All)
        {
            field.Set(result, field.Get(detail.Effective));
        }

        return result;
    }
}

/// <summary>
/// The global MCP settings. The result is a <see cref="GetMcpSettingsResult"/>: the resolved settings plus the stored
/// values and which fields a deployment lock or ceiling decides (the declared result stays <see cref="McpSettingsData"/>,
/// so a caller of 5.0 keeps working).
/// </summary>
[ProjectScopeExempt("security configuration, read by an Auditor or a SecurityAdmin", Policy = BeaconPermissions.AuditorOrSecurityAdminPolicy)]
public record GetMcpSettingsQuery : IRequest<McpSettingsData>;

/// <summary>
/// The global MCP settings as the settings page reads them. The inherited members are the RESOLVED settings every MCP
/// consumer without a project reads (deployment locks and ceilings applied); <see cref="Stored"/> is the row as saved,
/// which the page edits and sends back, so a value above a ceiling or under a lock survives a save.
/// </summary>
public sealed class GetMcpSettingsResult : McpSettingsData
{
    /// <summary>The global row as stored, before any lock or ceiling (the code defaults when nothing is stored).</summary>
    public McpSettingsData Stored { get; init; } = new();

    /// <summary>Fields a deployment lock pins: they cannot be changed globally or per project.</summary>
    public string[] LockedFields { get; init; } = [];

    /// <summary>Fields whose stored value a deployment ceiling lowered (or that no reader could use).</summary>
    public string[] ClampedFields { get; init; } = [];
}
