using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.SavedQueries;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services.Embed;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Beacon.Core.Handlers.Embed;

/// <summary>
/// Renders an embed grant's pinned query version for the embed token the request presents. The caller must be that token
/// (the embed middleware's principal for a verified token, acting for the grant's creator in the grant's one project, read
/// and run only, never with the Admin bypass), and the grant must still be live (<see cref="EmbedGrantResolver"/>). The
/// pinned version runs through the saved-query executor alone (<see cref="ISavedQueryRunner"/>): every step through
/// <c>IRowSecurityResolver</c> (the creator's entitlement, resolved live), <c>ISqlExecutionGate</c> and the read-only path,
/// the grant's fixed values and the token's locked values bound as database parameters, the project's data sources
/// re-checked right before the SQL. Rows are always PII-masked, whatever the project's detection setting, and capped at the
/// smaller of the project's row limit and <c>Beacon:Embed:MaxRows</c>. At most <c>Beacon:Embed:MaxConcurrentRenders</c>
/// renders run at once in the process, and at most <c>MaxConcurrentRendersPerGrant</c> for one grant; one more is refused at
/// once (<see cref="EmbedBusyException"/>). Any failure is the
/// one generic refusal (<see cref="EmbedUnavailableException"/>): no gate verdict, database error or table name reaches the
/// viewer. Records <c>embed.query_rendered</c> (ids, outcome, row count) for every render that reaches the grant, also when no
/// slot is free (<see cref="Busy"/>, within the embed audit cap) or the executor throws (<see cref="RenderFailed"/>).
/// </summary>
internal sealed class RenderEmbedQueryHandler(
    IBeaconActorAccessor actorAccessor,
    EmbedGrantResolver grants,
    ISavedQueryRunner runner,
    EmbedRenderLimiter renderLimiter,
    EmbedAuditLimiter auditLimiter,
    IOptions<EmbedOptions> embedOptions,
    ISecurityAuditRecorder securityAudit,
    ILogger<RenderEmbedQueryHandler> logger)
    : IRequestHandler<RenderEmbedQueryCommand, RenderEmbedQueryResult>
{
    public const string RenderFailed = "render_failed";
    public const string Unrunnable = "unrunnable";
    public const string ParametersRefused = "parameters";
    public const string Busy = "busy";

    public async Task<RenderEmbedQueryResult> Handle(RenderEmbedQueryCommand request, CancellationToken cancellationToken)
    {
        var options = embedOptions.Value;
        var caller = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (!options.Enabled
            || caller.Kind != BeaconPrincipalKind.Embed
            || caller.EmbedCredential != EmbedCredential.Token
            || caller.EmbedGrantId != request.GrantId
            || caller.BeaconUserId == null)
        {
            throw new EmbedUnavailableException(EmbedRefusalCodes.Unknown, caller.EmbedGrantId);
        }

        var resolution = await grants.ResolveIdAsync(request.GrantId, cancellationToken);
        if (!resolution.IsLive || resolution.Grant!.ProjectId != request.ProjectId)
        {
            throw new EmbedUnavailableException(resolution.Refusal ?? EmbedRefusalCodes.Unknown, request.GrantId);
        }

        var grant = resolution.Grant;
        var shape = SavedQueryToolRules.Inspect(grant.Version.StepsJson);
        if (shape.Issue != null)
        {
            await RecordAsync(grant, SecurityAuditOutcomes.Failure, Unrunnable, 0, false, cancellationToken);

            throw new EmbedUnavailableException(Unrunnable, grant.Id);
        }

        // The token carries exactly the grant's locked parameters; the grant fixes the others. Converted by type and bound
        // as database parameters, never written into the SQL.
        var locked = request.Parameters ?? new Dictionary<string, string>();
        var lockedMatch = locked.Count == grant.LockedParameterNames.Count
            && grant.LockedParameterNames.All(locked.ContainsKey);
        var supplied = new Dictionary<string, string>(grant.FixedParameters, StringComparer.Ordinal);
        foreach (var (name, value) in locked)
        {
            lockedMatch = lockedMatch && supplied.TryAdd(name, value);
        }

        var values = new Dictionary<string, object?>();
        var conversionError = lockedMatch ? SavedQueryArgumentConverter.ConvertAll(shape.Parameters, supplied, out values) : ParametersRefused;
        if (conversionError != null)
        {
            await RecordAsync(grant, SecurityAuditOutcomes.Refused, ParametersRefused, 0, false, cancellationToken);

            throw new EmbedUnavailableException(ParametersRefused, grant.Id);
        }

        var definition = new SavedQueryToolDefinition(
            grant.QueryId,
            "embed",
            grant.Name,
            grant.Name,
            grant.Version.VersionId,
            grant.Version.VersionNumber,
            shape.Steps,
            string.IsNullOrWhiteSpace(grant.Version.FinalQuery) ? null : grant.Version.FinalQuery,
            shape.Parameters,
            shape.Steps
                .Select(x => x.DataSourceId)
                .Distinct()
                .OrderBy(x => x)
                .ToList(),
            [grant.ProjectId]);

        var slot = renderLimiter.TryAcquire(grant.Id);
        if (slot == null)
        {
            // A saturated process answers many of these: bounded by the embed audit cap, logged past it.
            logger.LogWarning("Embed render of grant {GrantId} refused: no render slot is free", grant.Id);
            if (auditLimiter.TryAcquire())
            {
                await RecordAsync(grant, SecurityAuditOutcomes.Failure, Busy, 0, false, cancellationToken);
            }

            throw new EmbedBusyException();
        }

        using var lease = slot;

        SavedQueryToolExecution execution;
        try
        {
            execution = await runner.ExecuteAsync(
                definition,
                grant.ProjectId,
                values,
                new SavedQueryRunOptions(ForcePiiDetection: true, MaxRowsCeiling: options.MaxRows),
                cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A render that reached the executor always leaves its outcome in the trail; the endpoint answers the generic 503.
            logger.LogWarning("Embed render of grant {GrantId} could not complete", grant.Id);
            await RecordAsync(grant, SecurityAuditOutcomes.Failure, RenderFailed, 0, false, cancellationToken);

            throw;
        }

        if (!execution.Success)
        {
            // The executor's message can name a gate verdict, a table or a database error: logged by id only, never returned.
            logger.LogWarning("Embed render of grant {GrantId} failed", grant.Id);
            await RecordAsync(grant, SecurityAuditOutcomes.Failure, RenderFailed, 0, false, cancellationToken);

            throw new EmbedUnavailableException(RenderFailed, grant.Id);
        }

        await RecordAsync(grant, SecurityAuditOutcomes.Success, null, execution.Rows.Count, execution.Truncated, cancellationToken);

        var columns = execution.Rows.Count > 0
            ? execution.Rows[0].Keys.ToList()
            : [];

        return new RenderEmbedQueryResult(grant.Name, columns, execution.Rows, execution.Truncated, execution.MaxRows);
    }

    private Task RecordAsync(EmbedGrantState grant, string outcome, string? reason, int rowCount, bool truncated, CancellationToken cancellationToken)
    {
        var details = new SecurityAuditDetails()
            .Add("queryId", grant.QueryId)
            .Add("versionId", grant.QueryVersionId)
            .Add("rowCount", rowCount)
            .Add("truncated", truncated);
        if (reason != null)
        {
            details = details.AddCode("reason", reason);
        }

        return securityAudit.RecordAsync(
            new SecurityAuditEntry(SecurityAuditActions.EmbedQueryRendered)
                .On(SecurityAuditResources.EmbedGrant, grant.Id)
                .InProject(grant.ProjectId)
                .With(details)
                .Ending(outcome),
            cancellationToken);
    }
}

/// <summary>
/// Renders the grant the request's embed token names (<c>Authorization: BeaconEmbed &lt;token&gt;</c>). The endpoint fills
/// every property from the verified token, never from the request body: <see cref="ProjectId"/> and <see cref="GrantId"/>
/// are the grant's, <see cref="Parameters"/> the token's locked values.
/// </summary>
public sealed record RenderEmbedQueryCommand(
    int ProjectId,
    [property: ProjectScopeExempt("the grant the verified token names: the handler requires the caller to be that grant's token, and the grant's project is the check")]
    int GrantId,
    IReadOnlyDictionary<string, string>? Parameters)
    : IRequest<RenderEmbedQueryResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Project(ProjectId), ProjectAction.Run)
    ];
}

/// <param name="Rows">At most <see cref="MaxRows"/> rows, PII-masked.</param>
/// <param name="Truncated">True when the result had more than <see cref="MaxRows"/> rows.</param>
public sealed record RenderEmbedQueryResult(
    string Title,
    IReadOnlyList<string> Columns,
    IReadOnlyList<Dictionary<string, object?>> Rows,
    bool Truncated,
    int MaxRows);
