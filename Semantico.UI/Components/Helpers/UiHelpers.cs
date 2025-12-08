using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Recipients;

namespace Semantico.UI.Components;

public static class UiHelpers
{
    public const string MaskedDestinationValue = "********";

    /// <summary>
    /// Formats recipient destination for display based on notification type:
    /// - Jira recipients: Shows only the project key (e.g., "PROJ" instead of "domain;PROJ;email;api-key")
    /// - Teams recipients: Shows only the first 20 characters with "..." (e.g., "https://outlook.offi...")
    /// - Email recipients: Shows the full email address (unchanged)
    /// </summary>
    public static string FormatRecipientDestination(RecipientData recipient)
    {
        return recipient.NotificationType switch
        {
            NotificationType.Jira => ExtractJiraProject(recipient.Destination),
            NotificationType.Teams => recipient.Destination.Length > 20
                ? recipient.Destination.Substring(0, 20) + "..."
                : recipient.Destination,
            _ => recipient.Destination
        };
    }

    private static string ExtractJiraProject(string destination)
    {
        var parts = destination.Split(';');
        return parts.Length >= 2 ? parts[1] : destination;
    }
}
