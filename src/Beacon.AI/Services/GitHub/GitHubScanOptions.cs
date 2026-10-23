namespace Beacon.AI.Services.GitHub;

/// <summary>Bounds on one repository scan, bound from <c>Beacon:GitHub</c>.</summary>
public sealed class GitHubScanOptions
{
    public const string SectionName = "Beacon:GitHub";

    /// <summary>The most source files read from one repository; the rest are left out and the scan says so.</summary>
    public int MaxFilesPerRepository { get; set; } = 5000;

    /// <summary>The longest one repository scan runs; a scan that reaches it keeps what it found and says so.</summary>
    public TimeSpan ScanTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The largest source file (bytes) that is read; a larger file is skipped.</summary>
    public int MaxFileBytes { get; set; } = 1_000_000;

    /// <summary>The most bytes read from the repository tree response.</summary>
    public int MaxTreeResponseBytes { get; set; } = 32 * 1024 * 1024;
}
