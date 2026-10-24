namespace Beacon.Core.Configuration;

/// <summary>How exported reports (CSV and XLSX attachments) are written, bound from <c>Beacon:Reports</c>.</summary>
public sealed class ReportOptions
{
    public const string SectionName = "Beacon:Reports";

    /// <summary>
    /// A text cell that starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return is written so a
    /// spreadsheet reads it as text and never as a formula: a CSV cell gets a leading <c>'</c>, an XLSX cell the quote
    /// prefix. Numbers, dates and booleans are never touched. Default true; set false only when a consumer needs the
    /// exact text and is not a spreadsheet.
    /// </summary>
    public bool NeutralizeFormulas { get; set; } = true;
}
