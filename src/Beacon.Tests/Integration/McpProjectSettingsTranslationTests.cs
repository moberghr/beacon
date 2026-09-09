using NUnit.Framework;
using Beacon.Tests.Common;

namespace Beacon.Tests.Integration;

/// <summary>
/// SC6 of spec <c>mcp-project-settings</c>: the per-project lookup the effective-settings resolver runs
/// translates on Npgsql (§4.3–§4.5 — <c>ToQueryString()</c>, no database).
/// </summary>
[TestFixture]
public class McpProjectSettingsTranslationTests : QueryTranslationTestBase
{
    [Test]
    public void EffectiveSettingsLookup_Translates()
    {
        AssertQueryTranslates(context => context.McpProjectSettings
            .Where(x => x.ProjectId == 1));
    }

    [Test]
    public void ProjectSettingsWithProject_Translates()
    {
        AssertQueryTranslates(context => context.McpProjectSettings
            .Where(x => x.Project.ArchivedTime == null)
            .Select(x =>
                new
                {
                    x.ProjectId,
                    x.MaxRowLimit,
                    x.RetainQueryContent,
                    x.MaxExplainCost
                }));
    }

    [Test]
    public void SignalCallerHashFilter_Translates()
    {
        AssertQueryTranslates(context => context.McpQuerySignals
            .Where(x => x.CallerHash == "abc")
            .Select(x => x.Id));
    }
}
