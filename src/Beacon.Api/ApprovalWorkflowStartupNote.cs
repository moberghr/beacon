using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Notifications;
using Beacon.Core.Services;

namespace Beacon.Api;

/// <summary>
/// Notes once at startup how query changes are governed: the approval workflow off (a Warning outside Development, where
/// any change with write access goes live unreviewed), self-approval allowed (a Warning: one person can request, approve
/// and publish a change), and, with the workflow on, what subscription runs require (strict mode or not) and which other
/// changes — migration jobs, Custom SQL rules, recipient destinations, data-source connections, the LLM connection, grants
/// of the Admin role — wait for approval, and which the host left out. With the
/// workflow on, after the host has started — off the startup thread and best effort: a database that cannot be read is
/// noted, never a failure — it counts what the rules leave open: the queries never versioned (they run as stored outside
/// strict mode, each run logged); the queries with subscriptions whose stored SQL is not their active version's content
/// (every run is skipped); in strict mode, the queries with subscriptions or MCP tools whose active version is not approved
/// for its content hash (neither runs); with self-approval off, whether fewer than two users can approve a change through
/// their stored roles, and, while LLM connection changes are reviewed, whether fewer than two users are Admins; while
/// grants of the Admin role are reviewed (self-approval never applies to them), whether fewer than two users are Admins;
/// outside
/// strict mode, the MCP tools whose approval recorded no content hash. For each kind of change
/// the workflow does not cover now — every kind while it is off — it counts what that releases: the migration jobs, Custom
/// SQL rules and recipients the workflow governed that hold a definition nobody approved (created and never approved,
/// changed, or rejected) and the data sources held since their creation, which now run, are evaluated, receive deliveries
/// or connect as they are, and the pending requests of that kind (a staged LLM connection change is never applied
/// unreviewed, nor is a grant of the Admin role: only their pending requests are counted). Each Warning names the remedy. Configuration names, counts and tool
/// names only (§1.11).
/// </summary>
internal sealed class ApprovalWorkflowStartupNote(
    ILogger<ApprovalWorkflowStartupNote> logger,
    BeaconConfiguration? configuration = null,
    IHostEnvironment? environment = null,
    IOptions<RoleMappingOptions>? roleMapping = null) : IStartupFilter
{
    private static readonly (ChangeApprovalKind Kind, string Option, string Resources, string Effect, string Changes)[] GovernedKinds =
    [
        (ChangeApprovalKind.MigrationJob, nameof(ApprovalWorkflowOptions.CoversMigrationJobs), "migration jobs", "run", "new migration jobs"),
        (ChangeApprovalKind.CustomSqlRules, nameof(ApprovalWorkflowOptions.CoversCustomSqlRules), "enabled Custom SQL rules", "are evaluated", "new and changed Custom SQL rules"),
        (ChangeApprovalKind.RecipientDestination, nameof(ApprovalWorkflowOptions.CoversRecipientDestinations), "recipients", "receive deliveries", "new recipients and changed destinations"),
        (ChangeApprovalKind.DataSourceConnection, nameof(ApprovalWorkflowOptions.CoversDataSourceConnections), "data sources", "connect", "new data sources and changed connections"),
        (ChangeApprovalKind.LlmConnection, nameof(ApprovalWorkflowOptions.CoversLlmConnection), "LLM connection changes", "are not applied", "changes of the LLM connection"),
        (ChangeApprovalKind.AdminRoleGrant, nameof(ApprovalWorkflowOptions.CoversAdminRoleGrants), "Admin role grants", "are not applied", "grants of the Admin role")
    ];

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        if (configuration == null)
        {
            return next;
        }

        Note(logger, configuration.ApprovalWorkflow, environment?.IsDevelopment() == true);

        return builder =>
        {
            NoteConnectionResolver(logger, configuration.ApprovalWorkflow, builder.ApplicationServices);

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

        var kinds = new (bool Covered, string Change, string Option)[]
        {
            (workflow.CoversMigrationJobs, "new migration jobs (they do not run until approved)", nameof(ApprovalWorkflowOptions.CoversMigrationJobs)),
            (workflow.CoversCustomSqlRules, "new and changed Custom SQL rules (they are not evaluated until approved)", nameof(ApprovalWorkflowOptions.CoversCustomSqlRules)),
            (workflow.CoversRecipientDestinations, "new recipients and changed destinations (deliveries skip them until approved)", nameof(ApprovalWorkflowOptions.CoversRecipientDestinations)),
            (workflow.CoversDataSourceConnections, "new data sources and changed connections (they connect to nothing until approved)", nameof(ApprovalWorkflowOptions.CoversDataSourceConnections)),
            (workflow.CoversLlmConnection, "changes of the LLM connection's provider, endpoint, region or authentication mode (staged until a live Admin approves them)", nameof(ApprovalWorkflowOptions.CoversLlmConnection)),
            (workflow.CoversAdminRoleGrants, "grants of the Admin role (not applied until another live Admin approves them, whatever self-approval allows)", nameof(ApprovalWorkflowOptions.CoversAdminRoleGrants))
        };
        var covered = kinds
            .Where(x => x.Covered)
            .Select(x => x.Change)
            .ToList();
        var notCovered = kinds
            .Where(x => !x.Covered)
            .Select(x => nameof(BeaconConfiguration.ApprovalWorkflow) + "." + x.Option)
            .ToList();
        if (covered.Count > 0)
        {
            logger.LogInformation("The approval workflow also reviews {Changes}; changes made before it did run as before", string.Join("; ", covered));
        }

        if (notCovered.Count > 0)
        {
            logger.LogInformation("The approval workflow does not review the changes these options leave out: {Options}", string.Join(", ", notCovered));
        }
    }

    /// <summary>
    /// A Warning when the hold on data-source connections (<see cref="ApprovalWorkflowOptions.CoversDataSourceConnections"/>)
    /// is on while the registered <see cref="IDataSourceConnectionResolver"/> is not Beacon's own: the resolver is the only
    /// place the hold is enforced, so the host's resolver connects held sources while Beacon shows them as awaiting approval.
    /// </summary>
    internal static void NoteConnectionResolver(ILogger logger, ApprovalWorkflowOptions workflow, IServiceProvider services)
    {
        if (!workflow.Covers(ChangeApprovalKind.DataSourceConnection))
        {
            return;
        }

        object? resolver;
        try
        {
            resolver = services.GetService<IDataSourceConnectionResolver>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("The registered data-source connection resolver could not be checked ({ExceptionType})", ex.GetType().Name);

            return;
        }

        if (resolver == null || resolver.GetType().Assembly == typeof(IDataSourceConnectionResolver).Assembly)
        {
            return;
        }

        logger.LogWarning(
            "{Option} is on, but the registered {Resolver} is {ResolverType}, not Beacon's: the hold is enforced only inside Beacon's resolver, so data sources awaiting approval connect through it unheld. Have it refuse a source whose connection is not approved, or turn the option off",
            nameof(BeaconConfiguration.ApprovalWorkflow) + "." + nameof(ApprovalWorkflowOptions.CoversDataSourceConnections),
            nameof(IDataSourceConnectionResolver),
            resolver.GetType().Name);
    }

    /// <summary>The counts, logged; each read on its own, so one that cannot be read leaves the others.</summary>
    internal async Task NoteCountsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var contextFactory = services.GetService<IDbContextFactory<BeaconContext>>();
        var workflow = configuration?.ApprovalWorkflow;
        if (contextFactory == null || workflow == null)
        {
            return;
        }

        // What a switch that is off (or the workflow off) releases.
        foreach (var kind in GovernedKinds.Where(x => !workflow.Covers(x.Kind)))
        {
            await NoteAsync($"released {kind.Resources}", () => NoteReleasedAsync(services, contextFactory, workflow, kind, cancellationToken));
        }

        if (!workflow.Enabled)
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
            await NoteAsync("users who can approve", () => NoteApproversAsync(contextFactory, workflow, cancellationToken));

            if (workflow.Covers(ChangeApprovalKind.LlmConnection))
            {
                await NoteAsync("Admins who can approve an LLM connection change", () => NoteAdminsAsync(contextFactory, cancellationToken));
            }
        }

        // Self-approval never applies to a grant of the Admin role: fewer than two Admins leave every grant undecidable.
        if (workflow.Covers(ChangeApprovalKind.AdminRoleGrant) && configuration!.UserManagement.Enabled)
        {
            await NoteAsync("Admins who can approve a grant of the Admin role", () => NoteAdminGrantDecidersAsync(contextFactory, cancellationToken));
        }

        if (!workflow.RequireApprovedVersionToRun)
        {
            await NoteAsync("MCP tools approved without a content hash", () => NoteToolsOnApprovalsWithoutAHashAsync(contextFactory, cancellationToken));
        }
    }

    // Governed resources holding a definition nobody approved, and pending requests, of a kind the workflow does not cover
    // now: they work as they are, so the Warning names the switch that would hold them again.
    private async Task NoteReleasedAsync(
        IServiceProvider services,
        IDbContextFactory<BeaconContext> contextFactory,
        ApprovalWorkflowOptions workflow,
        (ChangeApprovalKind Kind, string Option, string Resources, string Effect, string Changes) kind,
        CancellationToken cancellationToken)
    {
        var protector = services.GetService<RecipientSecretProtector>();
        var hasher = services.GetService<RecipientDestinationHasher>();
        if (protector == null || hasher == null)
        {
            return;
        }

        var released = await ReleasedChangeReport.CountAsync(contextFactory, protector, hasher, kind.Kind, cancellationToken);
        if (released.UnapprovedResources == 0 && released.PendingRequests == 0)
        {
            return;
        }

        var option = nameof(BeaconConfiguration.ApprovalWorkflow) + "." + (workflow.Enabled ? kind.Option : nameof(ApprovalWorkflowOptions.Enabled));
        if (kind.Kind == ChangeApprovalKind.AdminRoleGrant)
        {
            logger.LogWarning(
                "{RequestCount} grants of the Admin role are pending, and with {Option} off they are not applied. Turn it on to decide them, or grant the role again",
                released.PendingRequests,
                option);

            return;
        }

        if (kind.Kind == ChangeApprovalKind.LlmConnection)
        {
            logger.LogWarning(
                "{RequestCount} staged changes of the LLM connection are pending, and with {Option} off nobody decides them: they are not applied. Turn it on to decide them",
                released.PendingRequests,
                option);

            return;
        }

        logger.LogWarning(
            "{ResourceCount} {Resources} the approval workflow governed hold a change nobody approved (new, changed or rejected), and {RequestCount} of their change requests are pending: with {Option} off they {Effect} as they are, unreviewed. Turn it on to hold them until they are approved",
            released.UnapprovedResources,
            kind.Resources,
            released.PendingRequests,
            option,
            kind.Effect);
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

    private async Task NoteApproversAsync(IDbContextFactory<BeaconContext> contextFactory, ApprovalWorkflowOptions workflow, CancellationToken cancellationToken)
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

        // What waits for a second person: query changes, and each other kind of change the workflow covers.
        var reviewed = GovernedKinds
            .Where(x => workflow.Covers(x.Kind))
            .Select(x => x.Changes)
            .Prepend("query changes")
            .ToList();
        logger.LogWarning(
            "{ApproverCount} active users can approve a change through their stored roles, and nobody approves their own change; the workflow holds {Changes} until a second person approves them: give a second user the {Role} role (or Admin), or turn on {Option} on a host with one approver",
            count,
            string.Join(", ", reviewed),
            mapping.RoleNameFor(BeaconPermissions.Approver),
            nameof(BeaconConfiguration.ApprovalWorkflow) + "." + nameof(ApprovalWorkflowOptions.AllowSelfApproval));
    }

    // Only a live Admin decides a change of the LLM connection, and nobody their own: fewer than two leave it undecidable.
    private async Task NoteAdminsAsync(IDbContextFactory<BeaconContext> contextFactory, CancellationToken cancellationToken)
    {
        var count = await ApprovalWorkflowReport.CountAdminsAsync(contextFactory, cancellationToken);
        if (count >= 2)
        {
            return;
        }

        logger.LogWarning(
            "{AdminCount} active users are Admins, and only an Admin other than the requester approves a change of the LLM connection ({Option}): no such change can be approved until a second user is an Admin, or {SelfApproval} is turned on",
            count,
            nameof(BeaconConfiguration.ApprovalWorkflow) + "." + nameof(ApprovalWorkflowOptions.CoversLlmConnection),
            nameof(BeaconConfiguration.ApprovalWorkflow) + "." + nameof(ApprovalWorkflowOptions.AllowSelfApproval));
    }

    // Only a live Admin other than the requester approves a grant of the Admin role, whatever self-approval allows.
    private async Task NoteAdminGrantDecidersAsync(IDbContextFactory<BeaconContext> contextFactory, CancellationToken cancellationToken)
    {
        var count = await ApprovalWorkflowReport.CountAdminsAsync(contextFactory, cancellationToken);
        if (count >= 2)
        {
            return;
        }

        logger.LogWarning(
            "{AdminCount} active users are Admins, and only an Admin other than the requester approves a grant of the Admin role ({Option}), whatever {SelfApproval} says: no grant can be approved until a second user is an Admin, or the option is turned off",
            count,
            nameof(BeaconConfiguration.ApprovalWorkflow) + "." + nameof(ApprovalWorkflowOptions.CoversAdminRoleGrants),
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
