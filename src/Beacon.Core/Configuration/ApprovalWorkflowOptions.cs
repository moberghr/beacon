namespace Beacon.Core;

public class ApprovalWorkflowOptions
{
    /// <summary>
    /// Enable approval workflow for query changes. Default: false (backward compatible, opt-in).
    /// When enabled, SQL changes to queries require admin approval before going live.
    /// </summary>
    public bool Enabled { get; set; } = false;
}
