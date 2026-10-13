using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Services;

namespace Beacon.Api;

/// <summary>
/// Notes once at startup how query changes are governed: the approval workflow off (a Warning outside Development, where
/// any change with write access goes live unreviewed), self-approval allowed (a Warning: one person can request, approve
/// and publish a change), and, with the workflow on, what subscription runs require (strict mode or not). With the
/// workflow on, after the host has started — off the startup thread and best effort: a database that cannot be read is
/// noted, never a failure — it counts what the rules leave open: the queries never versioned (they run as stored outside
/// strict mode, each run logged); the queries with subscriptions whose stored SQL is not their active version's content
/// (every run is skipped); in strict mode, the queries with subscriptions or MCP tools whose active version is not approved
/// for its content hash (neither runs); with self-approval off, whether fewer than two users can approve a change through
/// their stored roles; outside strict mode, the MCP tools whose approval recorded no content hash. Each Warning names the
/// remedy. Configuration names, counts and tool names only (§1.11).
/// </summary>
internal sealed class ApprovalWorkflowStartupNote(
    ILogger<ApprovalWorkflowStartupNote> logger,
    BeaconConfiguration? configuration = null,
    IHostEnvironment? environment = null,
    IOptions<RoleMappingOptions>? roleMapping = null) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        if (configuration == null)
        {
            return next;
        }

        Note(logger, configuration.ApprovalWorkflow, environment?.IsDevelopment() == true);

        if (!configuration.ApprovalWorkflow.Enabled)
        {
            return next;
        }

        return builder =>
        {
            var lifetime = builder.ApplicationServices.GetService<IHostApplicationLifetime>();
            lifetime?.ApplicationStarted.Register(
                () => _ = Task.Run(() => NoteCountsAsync(builder.ApplicationServices, lifetime.ApplicationStopping)));

            next(builder);
        };
    }

    internal static void Note(ILogger logger, ApprovalWorkflowOptions workflow, bool isDevelopment)
    {
        if (!workflow.Enabled)
        {
            logger.Log(
                isDevelopment ? LogLevel.Information : LogLevel.Warning,
                "The query change approval workflow is off ({Option}): a change to a query's SQL by anyone who may write it goes live without review. Turn it on with {Method}",
                nameof(BeaconConfiguration.ApprovalWorkflow) + "." + nameof(ApprovalWorkflowOptions.Enabled),
                nameof(BeaconConfiguration.EnableApprovalWorkflow));

            return;
        }

        if (workflow.AllowSelfApproval)
        {
            logger.LogWarning(
                "{Option} is on: the requester of a query change may approve it and publish the query as an MCP tool, and an AI actor's creator may approve its plans",
                nameof(BeaconConfiguration.ApprovalWorkflow) + "." + nameof(ApprovalWorkflowOptions.AllowSelfApproval));
        }

        logger.LogInformation(
            "The query change approval workflow is on; subscription runs {RunRule} ({Option})",
            workflow.RequireApprovedVersionToRun
                ? "require stored SQL that is the query's active version, approved for exactly its content"
                : "require stored SQL that is the query's active version's; a query never versioned runs as stored, and each such run is logged",
            nameof(BeaconConfiguration.ApprovalWorkflow) + "." + nameof(ApprovalWorkflowOptions.RequireApprovedVersionToRun));
    }

    /// <summary>The counts, logged; each read on its own, so one that cannot be read leaves the others.</summary>
    internal async Task NoteCountsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var contextFactory = services.GetService<IDbContextFactory<BeaconContext>>();
        var workflow = configuration?.ApprovalWorkflow;
        if (contextFactory == null || workflow is not { Enabled: true })
        {
            return;
        }

        await NoteAsync("queries never versioned", () => NoteUnversionedQueriesAsync(contextFactory, workflow, cancellationToken));
        await NoteAsync("queries whose stored SQL is not their active version", () => NoteDriftedScheduledQueriesAsync(contextFactory, cancellationToken));

        if (workflow.RequireApprovedVersionToRun)
        {
            await NoteAsync("active versions not approved for their content", () => NoteActiveVersionsNotApprovedAsync(contextFactory, cancellationToken));
        }

        if (!workflow.AllowSelfApproval && configuration!.UserManagement.Enabled)
        {
            await NoteAsync("users who can approve", () => NoteApproversAsync(contextFactory, cancellationToken));
        }

        if (!workflow.RequireApprovedVersionToRun)
        {
            await NoteAsync("MCP tools approved without a content hash", () => NoteToolsOnApprovalsWithoutAHashAsync(contextFactory, cancellationToken));
        }
    }

    private async Task NoteUnversionedQueriesAsync(IDbContextFactory<BeaconContext> contextFactory, ApprovalWorkflowOptions workflow, CancellationToken cancellationToken)
    {
        var count = await ApprovalWorkflowReport.CountUnversionedQueriesAsync(contextFactory, cancellationToken);
        if (count == 0)
        {
            return;
        }

        if (workflow.RequireApprovedVersionToRun)
        {
            logger.LogWarning(
                "{QueryCount} queries hold stored SQL that no version records and nobody reviewed: strict mode runs none of them until a save submits their SQL and it is approved",
                count);

            return;
        }

        logger.LogWarning(
            "{QueryCount} queries hold stored SQL that no version records and nobody reviewed: they run as stored, each run logged, until a save submits their SQL for approval",
            count);
    }

    private async Task NoteDriftedScheduledQueriesAsync(IDbContextFactory<BeaconContext> contextFactory, CancellationToken cancellationToken)
    {
        var count = await ApprovalWorkflowReport.CountDriftedScheduledQueriesAsync(contextFactory, cancellationToken);
        if (count == 0)
        {
            return;
        }

        logger.LogWarning(
            "{QueryCount} queries with subscriptions hold stored SQL that is not their active version's content: every run of them is skipped until a save submits the stored SQL for approval and it is approved, or a restored version is approved",
            count);
    }

    private async Task NoteActiveVersionsNotApprovedAsync(IDbContextFactory<BeaconContext> contextFactory, CancellationToken cancellationToken)
    {
        var count = await ApprovalWorkflowReport.CountActiveVersionsNotApprovedForTheirHashAsync(contextFactory, cancellationToken);
        if (count == 0)
        {
            return;
        }

        logger.LogWarning(
            "{QueryCount} queries with subscriptions or MCP tools have an active version that is not approved for its content hash (it went live while the workflow was off, or was approved before Beacon kept hashes): strict mode ({Option}) runs neither until the version is approved again — restore it and approve the restore",
            count,
            nameof(BeaconConfiguration.ApprovalWorkflow) + "." + nameof(ApprovalWorkflowOptions.RequireApprovedVersionToRun));
    }

    private async Task NoteApproversAsync(IDbContextFactory<BeaconContext> contextFactory, CancellationToken cancellationToken)
    {
        var mapping = roleMapping?.Value ?? new RoleMappingOptions();
        if (mapping.IdpRoles.Enabled)
        {
            // Permissions mapped from identity-provider roles are not stored, so a count of stored roles says too little.
            return;
        }

        var count = await ApprovalWorkflowReport.CountApproversAsync(contextFactory, mapping, cancellationToken);
        if (count >= 2)
        {
            return;
        }

        logger.LogWarning(
            "{ApproverCount} active users can approve a query change through their stored roles, and nobody approves their own change: give a second user the {Role} role (or Admin), or turn on {Option} on a host with one approver",
            count,
            mapping.RoleNameFor(BeaconPermissions.Approver),
            nameof(BeaconConfiguration.ApprovalWorkflow) + "." + nameof(ApprovalWorkflowOptions.AllowSelfApproval));
    }

    private async Task NoteToolsOnApprovalsWithoutAHashAsync(IDbContextFactory<BeaconContext> contextFactory, CancellationToken cancellationToken)
    {
        var (count, names) = await ApprovalWorkflowReport.ToolsOnApprovalsWithoutAHashAsync(contextFactory, cancellationToken);
        if (count == 0)
        {
            return;
        }

        logger.LogWarning(
            "{ToolCount} MCP tools run a version whose approval recorded no content hash (approved before Beacon kept hashes); strict mode ({Option}) runs them only once the version is approved again: {ToolNames}",
            count,
            nameof(BeaconConfiguration.ApprovalWorkflow) + "." + nameof(ApprovalWorkflowOptions.RequireApprovedVersionToRun),
            names);
    }

    // A count that cannot be read is noted, never a failure of the host.
    private async Task NoteAsync(string what, Func<Task> note)
    {
        try
        {
            await note();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("The approval workflow's startup count of {What} could not be read ({ExceptionType})", what, ex.GetType().Name);
        }
    }
}
