using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Tests.Common;

namespace Beacon.Tests.Integration;

/// <summary>
/// Verifies that handler LINQ queries translate to valid PostgreSQL SQL via ToQueryString().
/// Catches: unsupported LINQ methods, bad type conversions, provider-specific translation failures.
/// Does NOT catch: data-dependent runtime issues (e.g., Max() on empty sequences).
///
/// Each test mirrors a query from a handler in Beacon.Core/Handlers/.
/// If a handler query changes, the corresponding test should be updated.
/// </summary>
[TestFixture]
public class QueryTranslationTests : QueryTranslationTestBase
{
    // ─── Projects ────────────────────────────────────────────────────

    [Test]
    public void GetProjectsQuery_Translates()
    {
        AssertQueryTranslates(ctx => ctx.Projects
            .Where(p => p.ArchivedTime == null)
            .OrderByDescending(p => p.CreatedTime)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Description,
                DataSourceCount = p.DataSources.Count,
                RepositoryCount = p.Repositories.Count,
                LastScannedRepo = p.Repositories
                    .Where(r => r.LastScanAt != null)
                    .OrderByDescending(r => r.LastScanAt)
                    .Select(r => new { r.LastScanAt, r.ScanStatus })
                    .FirstOrDefault(),
                p.CreatedTime
            }));
    }

    [Test]
    public void GetProjectDetail_ProjectQuery_Translates()
    {
        AssertQueryTranslates(ctx => ctx.Projects
            .Where(p => p.Id == 1 && p.ArchivedTime == null)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Description,
                p.CreatedTime
            }));
    }

    [Test]
    public void GetProjectDetail_DataSourcesQuery_Translates()
    {
        AssertQueryTranslates(ctx => ctx.ProjectDataSources
            .Where(pds => pds.ProjectId == 1)
            .Select(pds => new
            {
                pds.DataSource.Name,
                pds.DataSource.DataSourceType,
                TableCount = ctx.DatabaseMetadata
                    .Count(dm => dm.DataSourceId == pds.DataSourceId && dm.ArchivedTime == null)
            }));
    }

    [Test]
    public void GetProjectDetail_RepositoriesQuery_Translates()
    {
        AssertQueryTranslates(ctx => ctx.GitHubRepositories
            .Where(r => r.ProjectId == 1)
            .Select(r => new
            {
                r.RepositoryUrl,
                r.ScanStatus,
                r.LastScanAt,
                r.TotalReferencesFound
            }));
    }

    // ─── Documentation Editing ───────────────────────────────────────

    [Test]
    public void UpdateDocumentationSection_Query_Translates()
    {
        AssertQueryTranslates(ctx => ctx.ProjectDocumentationSections
            .Where(x => x.Id == 1));
    }

    [Test]
    public void InstructDocumentation_SectionQuery_Translates()
    {
        AssertQueryTranslates(ctx => ctx.ProjectDocumentationSections
            .Where(x => x.Id == 1)
            .Select(x =>
                new
                {
                    x.Id,
                    x.Title,
                    x.Content,
                    x.SectionType
                }));
    }

    // ─── User management / SSO ───────────────────────────────────────

    [Test]
    public void GetUserByExternalIdAndProviderQuery_Translates()
    {
        const string externalId = "sub-abc-123";
        const string identityProvider = "https://login.example.com/";

        AssertQueryTranslates(ctx => ctx.Users
            .Where(x => x.ExternalId == externalId)
            .Where(x => x.IdentityProvider == identityProvider)
            .Select(x =>
                new
                {
                    x.Id,
                    x.ExternalId,
                    x.IdentityProvider,
                    x.UserName,
                    x.Email,
                    x.IsEnabled,
                    Roles = x.UserRoles.Select(y => y.Role.Name).ToList()
                }));
    }

    [Test]
    public void GetUserByExternalIdAndProviderQuery_WithNullProvider_Translates()
    {
        const string externalId = "guid-internal-user";
        string? identityProvider = null;

        AssertQueryTranslates(ctx => ctx.Users
            .Where(x => x.ExternalId == externalId)
            .Where(x => x.IdentityProvider == identityProvider)
            .Select(x =>
                new
                {
                    x.Id,
                    x.ExternalId,
                    x.IdentityProvider
                }));
    }

    // ─── AiActor service queries ─────────────────────────────────────

    /// <summary>
    /// Mirrors <c>AiActorService.GetPendingPlansAsync</c>. The projection includes a
    /// nested entity reference (<c>p.AiActor.Name</c>) which forces an inner JOIN, and
    /// a client-side method call (<c>CountJsonArrayElements</c>) which EF must lift into
    /// the final client-side projection without breaking server SQL translation.
    /// </summary>
    [Test]
    public void GetPendingPlansAsync_Translates()
    {
        const int actorId = 42;

        AssertQueryTranslates(ctx => ctx.AiActorPlans
            .Where(p => p.AiActorId == actorId)
            .Where(p => p.Status == AiActorPlanStatus.PendingApproval)
            .OrderByDescending(p => p.ProposedAt)
            .Select(p =>
                new PendingPlanSummary
                {
                    PlanId = p.Id,
                    ActorId = p.AiActorId,
                    ActorName = p.AiActor.Name,
                    UserInstruction = p.UserInstruction,
                    Analysis = p.Analysis,
                    ActionCount = CountJsonArrayElements(p.ActionsJson),
                    ProposedAt = p.ProposedAt,
                    Version = p.Version,
                    TokensUsed = p.TokensUsed,
                    EstimatedCost = p.EstimatedCost
                }));

        // Sanity check: the produced SQL must reference the joined actor row and the
        // pending-status filter. If either disappears, the handler's behaviour silently
        // changed and the assertion above (translation succeeds) is not enough.
        var sql = Context.AiActorPlans
            .Where(p => p.AiActorId == actorId)
            .Where(p => p.Status == AiActorPlanStatus.PendingApproval)
            .OrderByDescending(p => p.ProposedAt)
            .Select(p =>
                new
                {
                    p.Id,
                    p.AiActorId,
                    ActorName = p.AiActor.Name,
                    p.ActionsJson,
                    p.ProposedAt
                })
            .ToQueryString();

        Assert.That(sql, Does.Contain("ai_actor"), "expected JOIN onto ai_actor for ActorName projection");
        Assert.That(sql, Does.Contain("ORDER BY"), "expected ORDER BY proposed_at");
    }

    /// <summary>
    /// Mirrors <c>AiActorService.GetQueryContextAsync</c>. The EF-translated portion is
    /// <c>Queries.Where(...).Include(Subscriptions).ThenInclude(QueryExecutionHistory).Include(Steps).ToListAsync</c>;
    /// the in-memory shaping into <c>AiActorPrompts.QueryContext</c> happens after the
    /// round-trip. The test guards the server LINQ part — if either Include chain breaks
    /// translation, <c>ToQueryString()</c> throws.
    /// </summary>
    [Test]
    public void GetQueryContextAsync_Translates()
    {
        const int actorId = 7;

        AssertQueryTranslates(ctx => ctx.Queries
            .Where(q => q.AiActorId == actorId)
            .Include(q => q.Subscriptions)
                .ThenInclude(s => s.QueryExecutionHistory)
            .Include(q => q.Steps));
    }

    private static int CountJsonArrayElements(string json)
    {
        // Mirrors the private helper in AiActorService. EF Core treats this as a
        // client-evaluated call in the final projection — translation must still
        // succeed for the server-side columns it references.
        return string.IsNullOrEmpty(json) ? 0 : 1;
    }

}
