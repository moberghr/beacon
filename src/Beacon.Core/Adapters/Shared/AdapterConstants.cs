namespace Beacon.Core.Adapters.Shared;

/// <summary>
/// Shared constants used across notification adapters.
/// </summary>
internal static class AdapterConstants
{
    /// <summary>
    /// Notification prefix shown in titles across all channels.
    /// </summary>
    public const string NotificationPrefix = "[Beacon]";

    /// <summary>
    /// Display limits for Microsoft Teams notifications.
    /// </summary>
    public static class Teams
    {
        /// <summary>
        /// Maximum number of columns to display in Teams Adaptive Cards.
        /// Teams cards become cluttered with more than 3 columns.
        /// </summary>
        public const int MaxColumns = 3;

        /// <summary>
        /// Maximum number of rows to display in Teams notifications.
        /// </summary>
        public const int MaxRows = 10;
    }

    /// <summary>
    /// Display limits for Slack notifications.
    /// </summary>
    public static class Slack
    {
        /// <summary>
        /// Maximum number of columns to display in Slack code block tables.
        /// Optimal readability in Slack's fixed-width code blocks.
        /// </summary>
        public const int MaxColumns = 5;

        /// <summary>
        /// Maximum number of rows to display in Slack notifications.
        /// Balanced between detail and readability in Slack messages.
        /// </summary>
        public const int MaxRows = 25;

        /// <summary>
        /// Maximum width for individual columns in characters.
        /// </summary>
        public const int MaxColumnWidth = 20;

        /// <summary>
        /// Minimum width for columns to maintain alignment.
        /// </summary>
        public const int MinColumnWidth = 3;
    }

}
