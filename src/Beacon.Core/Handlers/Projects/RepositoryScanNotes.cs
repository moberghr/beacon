using Beacon.Core.Data.Enums;

namespace Beacon.Core.Handlers.Projects;

/// <summary>
/// How a scan that hit one of its limits (files, file size, time) is recorded: still <see cref="ScanStatus.Completed"/>,
/// with its reasons in <c>GitHubRepository.LastScanError</c>, so a partial scan is never shown as a full one. A completed
/// scan that hit no limit leaves the error empty.
/// </summary>
public static class RepositoryScanNotes
{
    private const string Prefix = "Partial scan: ";
    private const string Separator = "; ";

    /// <summary>The error text of a completed scan with <paramref name="reasons"/>; null when there are none.</summary>
    public static string? Format(IReadOnlyCollection<string> reasons) =>
        reasons.Count > 0 ? $"{Prefix}{string.Join(Separator, reasons)}." : null;

    /// <summary>The reasons a completed scan was partial; empty for a full scan and for any other status.</summary>
    public static List<string> PartialReasons(ScanStatus status, string? lastScanError)
    {
        if (status != ScanStatus.Completed || lastScanError == null || !lastScanError.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return [];
        }

        var text = lastScanError[Prefix.Length..];
        text = text.EndsWith('.') ? text[..^1] : text;
        return text
            .Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
