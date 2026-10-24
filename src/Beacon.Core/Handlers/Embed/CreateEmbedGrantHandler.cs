using System.Text.Json;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.SavedQueries;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services.Embed;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Beacon.Core.Handlers.Embed;

/// <summary>
/// Creates an embed grant: one project, one saved query and the query's runnable approved version
/// (<see cref="SavedQueryRunnableVersion"/>, strict mode included), the frame origins (<see cref="EmbedOriginRules"/>), an
/// expiry within <c>Beacon:Embed:MaxGrantDays</c>, and for every query parameter either a fixed value or the name the host's
/// backend supplies at each exchange. Embedding must be enabled. The version's data sources must all be linked to the
/// project, and the grant must be able to render: it acts for its creator (a signed-in user) in that one project, read and
/// run only and without the Admin bypass, so a creator who cannot run the project's queries that way (an Admin who is no
/// member of a restricted project) is refused. While the approval workflow is on, whoever requested or approved the version
/// may not embed it (unless the host allows self-approval), as for publishing an MCP tool. The secret is returned once;
/// only its hash is stored. The event is saved with the grant (ids and counts only).
/// </summary>
internal sealed class CreateEmbedGrantHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IBeaconActorAccessor actorAccessor,
    BeaconConfiguration configuration,
    IOptions<EmbedOptions> embedOptions,
    ISecurityAuditRecorder securityAudit,
    TimeProvider timeProvider,
    IServiceProvider services,
    ILogger<CreateEmbedGrantHandler> logger)
    : IRequestHandler<CreateEmbedGrantCommand, CreateEmbedGrantResult>
{
    public const int MaxNameLength = 100;

    /// <summary>The longest fixed parameter value, as for a saved-query tool's string argument.</summary>
    public const int MaxValueLength = SavedQueryArgumentConverter.MaxStringArgumentLength;

    public async Task<CreateEmbedGrantResult> Handle(CreateEmbedGrantCommand request, CancellationToken cancellationToken)
    {
        var options = embedOptions.Value;
        if (!options.Enabled)
        {
            throw new InvalidOperationException("Embedding is not enabled on this deployment (Beacon:Embed:Enabled).");
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
        {
            throw new InvalidOperationException($"An embed grant's name is 1–{MaxNameLength} characters.");
        }

        if (request.ExpiresInDays < 1 || request.ExpiresInDays > options.MaxGrantDays)
        {
            throw new InvalidOperationException($"An embed grant expires in 1 to {options.MaxGrantDays} days.");
        }

        var isDevelopment = services.GetService<IHostEnvironment>()?.IsDevelopment() == true;
        var origins = EmbedOriginRules.Normalize(request.AllowedOrigins, options.MaxOrigins, isDevelopment);

        var caller = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (caller.Kind != BeaconPrincipalKind.User || caller.BeaconUserId == null || caller.UserId == null)
        {
            throw AccessRefusal.Of(logger, caller, "project", request.ProjectId, "An embed grant is created by a signed-in Beacon user, for whom it acts.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var runnable = await SavedQueryRunnableVersion.ForQuery(context, request.QueryId, SavedQueryRunnableVersion.RequiresApprovedHash(configuration))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The query cannot be embedded: it has no approved active version. Submit it for approval and approve it first.");

        var shape = SavedQueryToolRules.Inspect(runnable.StepsJson);
        if (shape.Issue != null)
        {
            throw new InvalidOperationException($"The query cannot be embedded: {shape.Issue}");
        }

        var (fixedValues, lockedNames) = SplitParameters(shape.Parameters, request.FixedParameters, request.LockedParameters);

        var dataSourceIds = shape.Steps
            .Select(x => x.DataSourceId)
            .Distinct()
            .ToList();
        var linked = await context.ProjectDataSources
            .Where(x => x.ProjectId == request.ProjectId)
            .Where(x => dataSourceIds.Contains(x.DataSourceId))
            .Select(x => x.DataSourceId)
            .Distinct()
            .CountAsync(cancellationToken);
        if (linked != dataSourceIds.Count)
        {
            throw new InvalidOperationException("The query cannot be embedded in this project: the project does not link every data source the query reads.");
        }

        // The grant acts for its creator in this one project, read and run only, never with the Admin bypass.
        var asGrant = caller with
        {
            Kind = BeaconPrincipalKind.Embed,
            AllowedProjectIds = [request.ProjectId],
            ScopeCeiling = null,
            CallerKind = null,
            ApiKeyId = null,
            IsApiKey = false
        };
        if (!(await projectAccess.GetScopeAsync(asGrant, cancellationToken)).Allows(request.ProjectId, ProjectAction.Run))
        {
            throw new InvalidOperationException(
                "The grant would render nothing: it acts for you in this project without the Admin bypass, and you cannot run its queries there (as a member, or through an open project).");
        }

        // Only while the workflow is on: with it off nobody approves a change, so there is no second pair of eyes to keep.
        if (configuration.ApprovalWorkflow is { Enabled: true, AllowSelfApproval: false })
        {
            await EnsureNotRequesterOrApproverAsync(context, caller, request.QueryId, runnable.VersionId, cancellationToken);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var (secret, secretHash) = EmbedGrantSecret.Create();
        var grant = new EmbedGrant
        {
            PublicId = EmbedGrantSecret.NewPublicId(),
            ProjectId = request.ProjectId,
            QueryId = request.QueryId,
            QueryVersionId = runnable.VersionId,
            Name = name,
            AllowedOriginsJson = JsonSerializer.Serialize(origins),
            FixedParametersJson = JsonSerializer.Serialize(fixedValues),
            LockedParameterNamesJson = JsonSerializer.Serialize(lockedNames),
            SecretHash = secretHash,
            ExpiresTime = now.AddDays(request.ExpiresInDays),
            CreatedTime = now,
            CreatedByUserId = caller.UserId
        };

        // The save re-reads the query's runnable version in the same transaction: a version superseded since the lookup
        // above (or one that stopped being approved) is refused, never embedded.
        await securityAudit.SaveWithEventsAsync(
            context,
            async () =>
            {
                var stillRunnable = await SavedQueryRunnableVersion.ForQuery(context, request.QueryId, SavedQueryRunnableVersion.RequiresApprovedHash(configuration))
                    .Where(x => x.VersionId == runnable.VersionId)
                    .AnyAsync(cancellationToken);
                if (!stillRunnable)
                {
                    throw new InvalidOperationException("The query changed while the grant was created: its active approved version is no longer the one chosen. Try again.");
                }

                context.EmbedGrants.Add(grant);

                return await context.SaveChangesAsync(cancellationToken);
            },
            _ =>
            [
                new SecurityAuditEntry(SecurityAuditActions.EmbedGrantCreated)
                    .On(SecurityAuditResources.EmbedGrant, grant.Id)
                    .InProject(grant.ProjectId)
                    .With(new SecurityAuditDetails()
                        .Add("queryId", grant.QueryId)
                        .Add("versionId", grant.QueryVersionId)
                        .Add("originCount", origins.Count)
                        .Add("lockedParamCount", lockedNames.Count)
                        .Add("expiresDays", request.ExpiresInDays))
            ],
            cancellationToken);

        logger.LogInformation(
            "Embed grant {GrantId} created for query {QueryId} version {VersionId} in project {ProjectId} by user {UserId}",
            grant.Id,
            grant.QueryId,
            grant.QueryVersionId,
            grant.ProjectId,
            caller.BeaconUserId);

        return new CreateEmbedGrantResult(
            grant.Id,
            grant.PublicId,
            secret,
            grant.QueryVersionId,
            runnable.VersionNumber,
            grant.ExpiresTime,
            origins,
            lockedNames);
    }

    // Every parameter of the version is fixed (a value stored on the grant) or locked (supplied at each exchange), exactly
    // once; an unknown name, or a fixed value that is not of its parameter's type, is refused.
    private static (SortedDictionary<string, string> Fixed, List<string> Locked) SplitParameters(
        IReadOnlyList<SavedQueryToolParameter> parameters,
        IReadOnlyDictionary<string, string>? fixedParameters,
        IReadOnlyList<string>? lockedParameters)
    {
        var byName = parameters.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var fixedValues = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (parameterName, value) in fixedParameters ?? new Dictionary<string, string>())
        {
            if (!byName.TryGetValue(parameterName, out var parameter))
            {
                throw new InvalidOperationException($"The query has no parameter '{parameterName}'.");
            }

            // Converted as the render converts it, so a grant never fixes a value its query cannot bind.
            if (value == null || SavedQueryArgumentConverter.ConvertText(parameter, value, out _) != null)
            {
                throw new InvalidOperationException($"The fixed value of parameter '{parameterName}' is not a {TypeName(parameter.Type)}.");
            }

            fixedValues[parameterName] = value;
        }

        var locked = new List<string>();
        foreach (var parameterName in lockedParameters ?? [])
        {
            if (!byName.ContainsKey(parameterName))
            {
                throw new InvalidOperationException($"The query has no parameter '{parameterName}'.");
            }

            if (fixedValues.ContainsKey(parameterName) || locked.Contains(parameterName, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Parameter '{parameterName}' is named twice: it is either fixed or locked.");
            }

            locked.Add(parameterName);
        }

        var missing = parameters
            .Select(x => x.Name)
            .Where(x => !fixedValues.ContainsKey(x))
            .Where(x => !locked.Contains(x, StringComparer.Ordinal))
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Every query parameter is fixed or locked; not yet: {string.Join(", ", missing)}.");
        }

        locked.Sort(StringComparer.Ordinal);

        return (fixedValues, locked);
    }

    private static string TypeName(ParameterType type) =>
        type switch
        {
            ParameterType.Number => "number",
            ParameterType.DateTime => "ISO 8601 date-time",
            _ => "string"
        };

    // The approvals of the version name who asked for it and who approved it; neither embeds it.
    private async Task EnsureNotRequesterOrApproverAsync(BeaconContext context, BeaconActor caller, int queryId, int versionId, CancellationToken cancellationToken)
    {
        var approvals = await context.QueryApprovalRequests
            .Where(x => x.QueryVersionId == versionId)
            .Where(x => x.Status == ApprovalStatus.Approved)
            .Select(x =>
                new
                {
                    x.RequestedByUserId,
                    x.ReviewedByUserId
                })
            .ToListAsync(cancellationToken);

        if (approvals.Any(x => caller.IsRecordedAs(x.RequestedByUserId) || caller.IsRecordedAs(x.ReviewedByUserId)))
        {
            throw AccessRefusal.Of(
                logger,
                caller,
                "query",
                queryId,
                "A query is embedded by someone other than who requested or approved its active version.");
        }
    }
}

/// <summary>
/// Creates an embed grant (SecurityAdmin). <see cref="FixedParameters"/> fixes parameter values on the grant;
/// <see cref="LockedParameters"/> names the parameters the host's backend supplies at each token exchange (they travel in
/// the token, which its viewer can read: never secrets). Together they name every parameter of the query exactly once.
/// </summary>
public sealed record CreateEmbedGrantCommand : IRequest<CreateEmbedGrantResult>, IProjectAuthorizedRequest
{
    public int ProjectId { get; init; }

    public int QueryId { get; init; }

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    public int ExpiresInDays { get; init; }

    public IReadOnlyDictionary<string, string>? FixedParameters { get; init; }

    public IReadOnlyList<string>? LockedParameters { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Project(ProjectId), ProjectAction.Run),
        new(ProjectResource.Query(QueryId), ProjectAction.Read)
    ];
}

/// <param name="Secret">The grant secret, shown this once: only its hash is stored.</param>
public sealed record CreateEmbedGrantResult(
    int Id,
    string PublicId,
    string Secret,
    int QueryVersionId,
    int VersionNumber,
    DateTime ExpiresTime,
    IReadOnlyList<string> AllowedOrigins,
    IReadOnlyList<string> LockedParameters)
{
    // The compiler-generated ToString prints every member; the secrets are printed redacted.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Id = {Id}, PublicId = {PublicId}, Secret = {RedactedPrint.Of(Secret)}, QueryVersionId = {QueryVersionId}, VersionNumber = {VersionNumber}, ExpiresTime = {ExpiresTime}, AllowedOrigins = {AllowedOrigins}, LockedParameters = {LockedParameters}");

        return true;
    }
}
