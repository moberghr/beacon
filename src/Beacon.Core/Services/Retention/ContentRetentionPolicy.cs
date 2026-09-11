namespace Beacon.Core.Services.Retention;

/// <summary>
/// Reads the decision off the effective MCP settings (<c>projectId 0</c> = the global row for project-less writes).
/// Deployment locks and ceilings are already applied by <c>McpLockPolicy</c> inside the provider.
/// </summary>
internal sealed class ContentRetentionPolicy(IMcpSettingsProvider settingsProvider) : IContentRetentionPolicy
{
    public async Task<ContentRetentionDecision> ResolveAsync(int? projectId, CancellationToken ct = default)
    {
        var settings = await settingsProvider.GetEffectiveSettingsAsync(projectId ?? 0, ct);

        return ContentRetentionDecision.From(settings);
    }
}
