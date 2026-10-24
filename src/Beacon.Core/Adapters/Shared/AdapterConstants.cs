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

        /// <summary>Longest text shown in one cell, the query or a name in a card.</summary>
        public const int MaxCellLength = 200;

        public const int MaxQueryLength = 2000;

        /// <summary>
        /// Teams refuses a card larger than 28 KB; the default card keeps its serialized size under this, dropping rows
        /// that do not fit.
        /// </summary>
        public const int MaxCardBytes = 24_000;
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

        /// <summary>Slack refuses a section whose text is longer than 3,000 characters; stay under it.</summary>
        public const int MaxSectionTextLength = 2900;

        /// <summary>Slack refuses a header longer than 150 characters.</summary>
        public const int MaxHeaderLength = 150;

        /// <summary>Longest explanation of an anomaly shown in the alert.</summary>
        public const int MaxAnomalyExplanationLength = 1000;
    }

}
