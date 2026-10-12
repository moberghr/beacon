using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using McpSettingsEntity = Beacon.Core.Data.Entities.McpSettings;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// A data source several live projects link is held to the strictest guardrails among those projects, whichever of them
/// asks: read-only and PII detection on when any project turns them on, every project's custom PII patterns, the lowest
/// row, timeout, size and plan-cost limits. A source only the asking project links keeps its settings; archived projects
/// count for nothing. An Admin sees, per shared source, the effective guardrails and the project that sets each.
/// </summary>
[TestFixture]
public class SharedSourceGuardrailsTests
{
    // Project 1 (the asker) links 10 and 11; project 2 links 10; project 3 links 11; project 4 (archived) links 10 and 11.
    private const int Asker = 1;

    [Test]
    public void Tighten_TakesTheStrictestOfEachGuardrail_AndLeavesEverythingElse()
    {
        var own = new McpSettingsData
        {
            EnforceReadOnly = false,
            EnablePiiDetection = false,
            CustomPiiPatterns = ["iban"],
            MaxRowLimit = 1000,
            StatementTimeoutSeconds = 30,
            MaxResultBytes = 262144,
            MaxExplainCost = null,
            EnableLearning = true,
            RetainQueryContent = true
        };
        var other = new McpSettingsData
        {
            EnforceReadOnly = true,
            EnablePiiDetection = true,
            CustomPiiPatterns = ["iban", "oib"],
            MaxRowLimit = 50,
            StatementTimeoutSeconds = 10,
            MaxResultBytes = 1024,
            MaxExplainCost = 99.5m,
            EnableLearning = false,
            RetainQueryContent = false
        };

        McpGuardrails.Tighten(own, other);

        own.Should().BeEquivalentTo(new
        {
            EnforceReadOnly = true,
            EnablePiiDetection = true,
            CustomPiiPatterns = new[] { "iban", "oib" },
            MaxRowLimit = 50,
            StatementTimeoutSeconds = 10,
            MaxResultBytes = 1024,
            MaxExplainCost = (decimal?)99.5m,
            EnableLearning = true,
            RetainQueryContent = true
        }, x => x.ExcludingMissingMembers());
    }

    // Two plan-cost limits: the lower one applies, whichever project sets it.
    [TestCase(10.0, 99.5)]
    [TestCase(99.5, 10.0)]
    public void Tighten_KeepsTheLowerOfTwoPlanCostLimits(decimal own, decimal other)
    {
        var settings = new McpSettingsData { MaxExplainCost = own };

        McpGuardrails.Tighten(settings, new McpSettingsData { MaxExplainCost = other });

        settings.MaxExplainCost.Should().Be(10.0m);
        McpGuardrails.Describe([(1, new McpSettingsData { MaxExplainCost = own }), (2, new McpSettingsData { MaxExplainCost = other })])
            .Single(x => x.Setting == McpGuardrails.MaxExplainCost)
            .Should().Be((McpGuardrails.MaxExplainCost, "10", own < other ? 1 : 2));
    }

    // Grounding reads the source too: value grounding and sample values stay off when any linking project turns them off,
    // and the lowest probe budget applies.
    [Test]
    public void Tighten_CoversTheGroundingGuardrails()
    {
        var own = new McpSettingsData { EnableValueGrounding = true, ValueGroundingMaxProbes = 12, EnableSampleValueCollection = true };

        McpGuardrails.Tighten(own, new McpSettingsData { EnableValueGrounding = false, ValueGroundingMaxProbes = 3, EnableSampleValueCollection = false });

        own.EnableValueGrounding.Should().BeFalse();
        own.ValueGroundingMaxProbes.Should().Be(3);
        own.EnableSampleValueCollection.Should().BeFalse();

        var loosest = new McpSettingsData { EnableValueGrounding = false, ValueGroundingMaxProbes = 3, EnableSampleValueCollection = false };
        McpGuardrails.Tighten(loosest, new McpSettingsData { EnableValueGrounding = true, ValueGroundingMaxProbes = 12, EnableSampleValueCollection = true });
        loosest.EnableValueGrounding.Should().BeFalse("tightening never turns grounding back on");
        loosest.ValueGroundingMaxProbes.Should().Be(3);
        loosest.EnableSampleValueCollection.Should().BeFalse();
    }

    [Test]
    public void Describe_NamesTheProjectThatTurnsGroundingOff()
    {
        var described = McpGuardrails.Describe(
            [
                (1, new McpSettingsData { EnableValueGrounding = true, ValueGroundingMaxProbes = 12, EnableSampleValueCollection = true }),
                (2, new McpSettingsData { EnableValueGrounding = false, ValueGroundingMaxProbes = 12, EnableSampleValueCollection = true }),
                (3, new McpSettingsData { EnableValueGrounding = true, ValueGroundingMaxProbes = 4, EnableSampleValueCollection = false })
            ])
            .ToDictionary(x => x.Setting);

        described[McpGuardrails.EnableValueGrounding].Should().Be((McpGuardrails.EnableValueGrounding, "false", 2));
        described[McpGuardrails.ValueGroundingMaxProbes].Should().Be((McpGuardrails.ValueGroundingMaxProbes, "4", 3));
        described[McpGuardrails.EnableSampleValueCollection].Should().Be((McpGuardrails.EnableSampleValueCollection, "false", 3));
    }

    // Sampling a data source's values when its metadata is refreshed follows every live project that links it.
    [Test]
    public async Task ADataSourcesOwnWork_TakesTheStrictestOfTheLiveProjectsLinkingIt()
    {
        var provider = Provider(
            (1, new McpProjectSettings { ProjectId = 1, EnableSampleValueCollection = true, CustomPiiPatterns = """["iban"]""" }),
            (2, new McpProjectSettings { ProjectId = 2, EnableSampleValueCollection = false, CustomPiiPatterns = """["oib"]""" }),
            (4, new McpProjectSettings { ProjectId = 4, CustomPiiPatterns = """["archived"]""" }));

        var onTen = await provider.GetDataSourceSettingsAsync(10);
        var onTwelve = await provider.GetDataSourceSettingsAsync(12);

        onTen.EnableSampleValueCollection.Should().BeFalse("project 2 links source 10 and turns sample values off");
        onTen.CustomPiiPatterns.Should().BeEquivalentTo(["iban", "oib"], "the archived project 4 counts for nothing");
        onTwelve.EnableSampleValueCollection.Should().BeTrue();
        onTwelve.CustomPiiPatterns.Should().Equal("iban");
    }

    [Test]
    public void Tighten_NeverLoosens()
    {
        var own = new McpSettingsData { EnforceReadOnly = true, EnablePiiDetection = true, MaxRowLimit = 5, StatementTimeoutSeconds = 3, MaxResultBytes = 10, MaxExplainCost = 1m };

        McpGuardrails.Tighten(own, new McpSettingsData { EnforceReadOnly = false, EnablePiiDetection = false, MaxRowLimit = 5000, MaxExplainCost = null });

        own.Should().BeEquivalentTo(new { EnforceReadOnly = true, EnablePiiDetection = true, MaxRowLimit = 5, StatementTimeoutSeconds = 3, MaxResultBytes = 10, MaxExplainCost = (decimal?)1m }, x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task ASharedSource_TakesTheStrictestSettingsOfTheLiveProjectsLinkingIt()
    {
        var provider = Provider(
            (1, new McpProjectSettings { ProjectId = 1, MaxRowLimit = 500, EnablePiiDetection = false, CustomPiiPatterns = """["iban"]""" }),
            (2, new McpProjectSettings { ProjectId = 2, MaxRowLimit = 20, CustomPiiPatterns = """["oib"]""" }),
            (3, new McpProjectSettings { ProjectId = 3, StatementTimeoutSeconds = 5, EnablePiiDetection = true }),
            (4, new McpProjectSettings { ProjectId = 4, MaxRowLimit = 1, EnforceReadOnly = true }));

        var onTen = await provider.GetEffectiveSettingsAsync(Asker, [10]);
        var onEleven = await provider.GetEffectiveSettingsAsync(Asker, [11]);
        var onBoth = await provider.GetEffectiveSettingsAsync(Asker, [10, 11]);
        var ownSource = await provider.GetEffectiveSettingsAsync(Asker, [12]);

        onTen.MaxRowLimit.Should().Be(20, "project 2 links source 10 and allows 20 rows; the archived project 4 counts for nothing");
        onTen.CustomPiiPatterns.Should().BeEquivalentTo(["iban", "oib"]);
        onTen.EnablePiiDetection.Should().BeTrue("project 2 inherits PII detection on from the global settings");
        onTen.StatementTimeoutSeconds.Should().Be(30, "project 3 does not link source 10");
        onEleven.MaxRowLimit.Should().Be(500);
        onEleven.StatementTimeoutSeconds.Should().Be(5);
        onBoth.MaxRowLimit.Should().Be(20);
        onBoth.StatementTimeoutSeconds.Should().Be(5);
        ownSource.MaxRowLimit.Should().Be(500, "a source only the asking project links keeps its settings");
        ownSource.EnablePiiDetection.Should().BeFalse();
        ownSource.CustomPiiPatterns.Should().Equal("iban");
    }

    [Test]
    public async Task WithoutDataSources_TheProjectsOwnSettingsApply()
    {
        var provider = Provider((1, new McpProjectSettings { ProjectId = 1, MaxRowLimit = 500 }), (2, new McpProjectSettings { ProjectId = 2, MaxRowLimit = 20 }));

        (await provider.GetEffectiveSettingsAsync(Asker, [])).MaxRowLimit.Should().Be(500);
    }

    [Test]
    public async Task AnAdmin_SeesPerSharedSource_TheEffectiveGuardrails_AndWhoSetsEach()
    {
        var settings = SettingsProviderMock.Create(projectSettings: new Dictionary<int, McpSettingsData>
        {
            [1] = new() { MaxRowLimit = 500, EnablePiiDetection = false, CustomPiiPatterns = ["iban"] },
            [2] = new() { MaxRowLimit = 20, CustomPiiPatterns = ["iban"] },
            [3] = new() { MaxRowLimit = 500, StatementTimeoutSeconds = 5, CustomPiiPatterns = ["oib"] }
        });
        await using var context = await World().CreateDbContextAsync();

        var entries = await SharedSourceGuardrails.ReadAsync(context, settings.Object, Asker, CancellationToken.None);

        entries.Select(x => x.DataSourceId).Should().Equal(10, 11);
        var ten = entries[0].Guardrails.ToDictionary(x => x.Setting);
        ten[McpGuardrails.MaxRowLimit].Should().Be(new SharedSourceGuardrail(McpGuardrails.MaxRowLimit, "20", 2, "Payroll"));
        ten[McpGuardrails.EnablePiiDetection].Should().Be(new SharedSourceGuardrail(McpGuardrails.EnablePiiDetection, "true", 2, "Payroll"));
        ten[McpGuardrails.EnforceReadOnly].ProjectId.Should().Be(Asker, "the asking project already holds the strictest value");
        ten[McpGuardrails.CustomPiiPatterns].Should().Be(new SharedSourceGuardrail(McpGuardrails.CustomPiiPatterns, "1", Asker, "Finance"));
        ten[McpGuardrails.MaxExplainCost].Value.Should().Be("none");
        var eleven = entries[1].Guardrails.ToDictionary(x => x.Setting);
        eleven[McpGuardrails.MaxRowLimit].ProjectId.Should().Be(Asker, "a tie keeps the asking project");
        eleven[McpGuardrails.StatementTimeoutSeconds].Should().Be(new SharedSourceGuardrail(McpGuardrails.StatementTimeoutSeconds, "5", 3, "Audit"));
        eleven[McpGuardrails.CustomPiiPatterns].Should().Be(new SharedSourceGuardrail(McpGuardrails.CustomPiiPatterns, "2", 3, "Audit"));
        entries.Should().OnlyContain(x => x.Guardrails.Count == 10);
    }

    // What the Access card shows is what a caller of the source gets.
    [Test]
    public void TheDescribedValues_AreTheOnesThatApply()
    {
        var perProject = new Dictionary<int, McpSettingsData>
        {
            [1] = new() { MaxRowLimit = 500, EnablePiiDetection = false, CustomPiiPatterns = ["iban"] },
            [2] = new() { MaxRowLimit = 20, MaxExplainCost = 7m, CustomPiiPatterns = ["oib"] }
        };
        var applied = perProject[1].Clone();
        McpGuardrails.Tighten(applied, perProject[2]);

        var described = McpGuardrails.Describe([(1, perProject[1]), (2, perProject[2])]).ToDictionary(x => x.Setting);

        described[McpGuardrails.MaxRowLimit].Value.Should().Be(applied.MaxRowLimit.ToString());
        described[McpGuardrails.EnablePiiDetection].Value.Should().Be("true");
        described[McpGuardrails.MaxExplainCost].Value.Should().Be("7");
        described[McpGuardrails.CustomPiiPatterns].Value.Should().Be(applied.CustomPiiPatterns.Count.ToString());
    }

    [Test]
    public async Task OnlyTheProjectsSourcesSharedWithAnotherLiveProject_AreListed()
    {
        await using var context = await World(shareNothing: true).CreateDbContextAsync();

        var entries = await SharedSourceGuardrails.ReadAsync(context, SettingsProviderMock.Create().Object, Asker, CancellationToken.None);

        entries.Should().BeEmpty();
    }

    // The host's settings provider resolves each project; the shared-source tightening is Beacon's own.
    private static SharedSourceSettings Provider(params (int ProjectId, McpProjectSettings Row)[] rows)
    {
        var world = World(projectSettings: rows.Select(x => x.Row).ToList());
        var settingsProvider = new McpSettingsProvider(world, new MemoryCache(new MemoryCacheOptions()), Options.Create(new McpDeploymentOptions { ForceReadOnly = false, ForcePiiDetection = false }), NullLogger<McpSettingsProvider>.Instance);

        return new SharedSourceSettings(world, settingsProvider);
    }

    private static IDbContextFactory<BeaconContext> World(bool shareNothing = false, List<McpProjectSettings>? projectSettings = null)
    {
        var sources = new[] { 10, 11, 12 }
            .Select(x => new DataSource { Id = x, Name = $"source {x}", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "enc" })
            .ToDictionary(x => x.Id);
        var projects = new List<Project>
        {
            Project(1, "Finance", null, sources[10], sources[11], sources[12]),
            Project(2, "Payroll", null, shareNothing ? [] : [sources[10]]),
            Project(3, "Audit", null, shareNothing ? [] : [sources[11]]),
            Project(4, "Archived", DateTime.UtcNow, sources[10], sources[11])
        };
        var links = projects.SelectMany(x => x.DataSources).ToList();
        // The projects as the database returns them: its soft-delete filter leaves the archived project 4 out (the filter
        // in the SQL is pinned by ScopedLookupTranslationTests on both providers).
        var liveProjects = projects
            .Where(x => x.ArchivedTime == null)
            .ToList();
        var saved = new List<object>();

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(Project)] = RecordingBeaconContext.MemorySet(liveProjects, saved),
                    [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(links, saved),
                    [typeof(McpSettingsEntity)] = RecordingBeaconContext.MemorySet(new List<McpSettingsEntity>(), saved),
                    [typeof(McpProjectSettings)] = RecordingBeaconContext.MemorySet(projectSettings ?? [], saved)
                },
                saved,
                new SqlCapture()));

        return factory.Object;
    }

    private static Project Project(int id, string name, DateTime? archivedTime, params DataSource[] sources)
    {
        var project = new Project { Id = id, Name = name, ArchivedTime = archivedTime };
        project.DataSources = sources
            .Select(x => new ProjectDataSource { ProjectId = id, Project = project, DataSourceId = x.Id, DataSource = x })
            .ToList();

        return project;
    }
}
