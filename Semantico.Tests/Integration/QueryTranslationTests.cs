using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Semantico.Core.Data.Enums;
using Semantico.Tests.Common;

namespace Semantico.Tests.Integration;

/// <summary>
/// Verifies that handler LINQ queries translate to valid PostgreSQL SQL via ToQueryString().
/// Catches: unsupported LINQ methods, bad type conversions, provider-specific translation failures.
/// Does NOT catch: data-dependent runtime issues (e.g., Max() on empty sequences).
///
/// Each test mirrors a query from a handler in Semantico.Core/Handlers/.
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

}
