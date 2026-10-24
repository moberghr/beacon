using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Handlers.McpSettings;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using McpSettingsEntity = Beacon.Core.Data.Entities.McpSettings;

namespace Beacon.Tests.Unit;

/// <summary>
/// Every MCP setting, one by one: the value stored in the global row, the code default without a row, the value a
/// deployment lock forces, a project's override, and a save through each update handler read back through the settings
/// provider (with its cache). Driven by reflection over the settings types, so a field added to them is covered at once.
/// </summary>
[TestFixture]
public class McpSettingFieldParityTests
{
    private const int ProjectId = 42;

    // The global settings, in the order the settings types declare them.
    private static readonly string[] GlobalFields =
    [
        "AskSystemPrompt", "GlobalInstruction", "GetContextDescription", "QueryDescription", "GetDocumentationDescription",
        "AskDescription", "SearchDescription", "MaxRowLimit", "EnforceReadOnly", "EnablePiiDetection", "CustomPiiPatterns",
        "EnableSampleValueCollection", "EnableLearning", "LearningAutoApproveThreshold", "LearningInjectionBudgetChars",
        "LearningSignalRetentionDays", "EnableSelfConsistency", "SelfConsistencyCandidateCount", "EnableEvalJudge",
        "EnableSemanticRetrieval", "ExemplarTopK", "EnableReplayVerification", "LearningReplayMinFlips",
        "EnableContextualRetrieval", "DocChunkWindowSentences", "DocChunkOverlapSentences", "GlossaryTopK", "DocChunkTopK",
        "EnableGoldenExemplars", "GoldenExemplarTopK", "GoldenExemplarBudgetChars", "EnableValueGrounding",
        "ValueGroundingMaxProbes", "EnableSemanticLint", "SelfConsistencyMinTables", "RetainQueryContent",
        "StatementTimeoutSeconds", "MaxResultBytes", "MaxExplainCost", "MaxConcurrentQueriesPerKey",
        "AllowExplicitFeedbackContent"
    ];

    // The prompts and tool descriptions stay global: no project is known when the tools are listed.
    private static readonly string[] GlobalOnlyFields =
    [
        "AskSystemPrompt", "GlobalInstruction", "GetContextDescription", "QueryDescription", "GetDocumentationDescription",
        "AskDescription", "SearchDescription"
    ];

    private List<McpSettingsEntity> _globalRows = null!;
    private List<McpProjectSettings> _projectRows = null!;
    private List<AppSettingHistory> _history = null!;
    private MemoryCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _globalRows = [];
        _projectRows = [];
        _history = [];
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
    }

    private static IEnumerable<string> AllFields() => GlobalFields;

    private static IEnumerable<string> OverridableFields() => GlobalFields.Except(GlobalOnlyFields);

    [Test]
    public void TheSettingsTypes_CarryExactlyTheKnownFields()
    {
        SettingNames(typeof(McpSettingsData)).Should().Equal(GlobalFields);
        SettingNames(typeof(McpSettingsEntity), "Id", "CreatedTime").Should().BeEquivalentTo(GlobalFields);
        SettingNames(typeof(McpProjectSettingsData)).Should().BeEquivalentTo(OverridableFields());
        SettingNames(typeof(McpProjectSettings), "Id", "CreatedTime", "ProjectId", "Project").Should().BeEquivalentTo(OverridableFields());
    }

    // The descriptor table every mapping, write, overlay, range and history row goes through names each field once.
    [Test]
    public void TheSettingFieldTable_ListsEveryField_AndTheOverridableOnes()
    {
        McpSettingFields.All.Select(x => x.Name).Should().Equal(GlobalFields);
        McpSettingFields.Overridables.Select(x => x.Name).Should().Equal(OverridableFields());
        McpSettingFields.All.Where(x => x.RequiresPositive).Select(x => x.Name).Should().Equal("StatementTimeoutSeconds", "MaxResultBytes", "MaxConcurrentQueriesPerKey");
        McpSettingsRanges.Fields.Should().OnlyContain(x => McpSettingFields.Named(x).Overridable);
    }

    [TestCaseSource(nameof(AllFields))]
    public async Task AStoredGlobalValue_IsReadAsStored(string field)
    {
        var row = new McpSettingsEntity { Id = 1 };
        SetRow(row, field, Distinct(field, 0));
        _globalRows.Add(row);

        var settings = await CreateProvider(NoLocks()).GetSettingsAsync();

        Read(settings, field).Should().BeEquivalentTo(Distinct(field, 0));
    }

    [TestCaseSource(nameof(AllFields))]
    public async Task WithoutARow_EveryField_IsTheCodeDefault_AndTheNewRowStartsWithIt(string field)
    {
        var settings = await CreateProvider(NoLocks()).GetSettingsAsync();
        var defaults = new McpSettingsData();
        var newRow = new McpSettingsEntity();

        Read(settings, field).Should().BeEquivalentTo(Read(defaults, field));
        RowValue(newRow, field).Should().BeEquivalentTo(Read(defaults, field));
    }

    [TestCaseSource(nameof(OverridableFields))]
    public async Task AProjectOverride_IsReadForThatProject_AndAnEmptyOverrideInherits(string field)
    {
        var global = new McpSettingsEntity { Id = 1 };
        SetRow(global, field, Distinct(field, 0));
        _globalRows.Add(global);
        var overridden = new McpProjectSettings { Id = 7, ProjectId = ProjectId };
        SetRow(overridden, field, Distinct(field, 1));
        _projectRows.Add(overridden);
        _projectRows.Add(new McpProjectSettings { Id = 8, ProjectId = ProjectId + 1 });
        var provider = CreateProvider(NoLocks());

        var effective = await provider.GetEffectiveSettingsAsync(ProjectId);
        var inherited = await provider.GetEffectiveSettingsAsync(ProjectId + 1);
        var overrides = await provider.GetProjectOverridesAsync(ProjectId);

        Read(effective, field).Should().BeEquivalentTo(Distinct(field, 1));
        Read(inherited, field).Should().BeEquivalentTo(Distinct(field, 0));
        Read(overrides!, field).Should().BeEquivalentTo(Distinct(field, 1));
    }

    [TestCaseSource(nameof(GlobalOnlyFieldNames))]
    public async Task AGlobalOnlyField_IsTheGlobalValue_ForEveryProject(string field)
    {
        var global = new McpSettingsEntity { Id = 1 };
        SetRow(global, field, Distinct(field, 0));
        _globalRows.Add(global);
        _projectRows.Add(new McpProjectSettings { Id = 7, ProjectId = ProjectId, MaxRowLimit = 5 });

        var effective = await CreateProvider(NoLocks()).GetEffectiveSettingsAsync(ProjectId);

        Read(effective, field).Should().Be(Distinct(field, 0));
    }

    [TestCaseSource(nameof(LockCases))]
    public async Task ALockedField_IsForced_OverTheStoredValueAndAProjectOverride(McpDeploymentOptions options, string field, bool pinned)
    {
        var global = new McpSettingsEntity { Id = 1 };
        SetRow(global, field, !pinned);
        _globalRows.Add(global);
        var overridden = new McpProjectSettings { Id = 7, ProjectId = ProjectId };
        SetRow(overridden, field, !pinned);
        _projectRows.Add(overridden);
        var provider = CreateProvider(options);

        var settings = await provider.GetSettingsAsync();
        var detail = await provider.GetEffectiveSettingsDetailAsync(ProjectId);

        Read(settings, field).Should().Be(pinned);
        Read(detail.Effective, field).Should().Be(pinned);
        detail.LockedFields.Should().Contain(field);
    }

    [TestCaseSource(nameof(AllFields))]
    public async Task AGlobalSave_WritesEveryField_AndTheProviderReadsItBack(string field)
    {
        var row = new McpSettingsEntity { Id = 1 };
        _globalRows.Add(row);
        var provider = CreateProvider(NoLocks());
        var before = await provider.GetSettingsAsync();
        var data = new McpSettingsData();
        Write(data, field, Distinct(field, 0));

        await CreateGlobalHandler(NoLocks(), provider).Handle(new UpdateMcpSettingsCommand(data), CancellationToken.None);
        var after = await provider.GetSettingsAsync();

        Read(before, field).Should().BeEquivalentTo(Read(new McpSettingsData(), field));
        Read(after, field).Should().BeEquivalentTo(Distinct(field, 0), "the write invalidates the cached settings");
        _history.Select(x => x.SettingKey).Should().Contain($"Mcp.{field}");
    }

    [TestCaseSource(nameof(OverridableFields))]
    public async Task AProjectSave_WritesEveryOverride_AndTheProviderReadsItBack(string field)
    {
        var provider = CreateProvider(NoLocks());
        var before = await provider.GetEffectiveSettingsAsync(ProjectId);
        var data = new McpProjectSettingsData();
        Write(data, field, Distinct(field, 1));

        await CreateProjectHandler(NoLocks(), provider).Handle(new UpdateMcpProjectSettingsCommand(ProjectId, data), CancellationToken.None);
        var after = await provider.GetEffectiveSettingsAsync(ProjectId);

        Read(before, field).Should().BeEquivalentTo(Read(new McpSettingsData(), field));
        Read(after, field).Should().BeEquivalentTo(Distinct(field, 1));
        _projectRows.Should().ContainSingle();
        _history.Select(x => x.SettingKey).Should().Contain($"Mcp.Project.{ProjectId}.{field}");
    }

    // The default deployment locks bind an Admin too: a save cannot turn them off, and one that leaves them as pinned
    // keeps the values stored before the lock, which stay overridden.
    [TestCase("EnforceReadOnly")]
    [TestCase("EnablePiiDetection")]
    public async Task UnderTheDefaultLocks_AnAdminsSave_KeepsTheStoredSafetyFlag_AndItStaysForced(string field)
    {
        var row = new McpSettingsEntity { Id = 1, EnforceReadOnly = false, EnablePiiDetection = false };
        _globalRows.Add(row);
        var provider = CreateProvider(new McpDeploymentOptions());
        var handler = CreateGlobalHandler(new McpDeploymentOptions(), provider);
        var contradicting = new McpSettingsData { MaxRowLimit = 5 };
        Write(contradicting, field, false);

        var refused = () => handler.Handle(new UpdateMcpSettingsCommand(contradicting), CancellationToken.None);
        await handler.Handle(new UpdateMcpSettingsCommand(new McpSettingsData { MaxRowLimit = 5 }), CancellationToken.None);
        var settings = await provider.GetSettingsAsync();

        (await refused.Should().ThrowAsync<SettingLockedException>()).Which.FieldName.Should().Be(field);
        RowValue(row, field).Should().Be(false, "the stored value survives the lock");
        row.MaxRowLimit.Should().Be(5);
        Read(settings, field).Should().Be(true, "the lock forces it on");
    }

    // The global GET: the resolved settings every MCP consumer reads (locks and ceilings applied), unchanged since 5.0, plus
    // the stored values the page edits and the fields a lock or a ceiling decides.
    [Test]
    public async Task TheGlobalGet_ReturnsTheResolvedSettings_TheStoredValues_AndTheLockMetadata()
    {
        _globalRows.Add(new McpSettingsEntity { Id = 1, EnforceReadOnly = false, MaxRowLimit = 9000, CustomPiiPatterns = "[\"iban\"]", AskSystemPrompt = "Be brief." });
        var options = new McpDeploymentOptions { ForceReadOnly = true, ForcePiiDetection = true, Ceilings = new McpCeilingOptions { MaxRowLimit = 2000 } };
        var handler = new GetMcpSettingsHandler(CreateProvider(options));

        var result = await handler.Handle(new GetMcpSettingsQuery(), CancellationToken.None);
        var json = JsonSerializer.SerializeToElement<object>(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var resolved = """
            {"askSystemPrompt":"Be brief.","globalInstruction":null,"getContextDescription":null,"queryDescription":null,
            "getDocumentationDescription":null,"askDescription":null,"searchDescription":null,"maxRowLimit":2000,
            "enforceReadOnly":true,"enablePiiDetection":true,"customPiiPatterns":["iban"],"enableSampleValueCollection":false,
            "enableLearning":true,"learningAutoApproveThreshold":0.7,"learningInjectionBudgetChars":1500,
            "learningSignalRetentionDays":90,"enableSelfConsistency":false,"selfConsistencyCandidateCount":5,
            "enableEvalJudge":false,"enableSemanticRetrieval":true,"exemplarTopK":4,"enableReplayVerification":true,
            "learningReplayMinFlips":1,"enableContextualRetrieval":false,"docChunkWindowSentences":5,
            "docChunkOverlapSentences":1,"glossaryTopK":5,"docChunkTopK":6,"enableGoldenExemplars":true,
            "goldenExemplarTopK":4,"goldenExemplarBudgetChars":2000,"enableValueGrounding":false,
            "valueGroundingMaxProbes":12,"enableSemanticLint":true,"selfConsistencyMinTables":2,"retainQueryContent":true,
            "statementTimeoutSeconds":30,"maxResultBytes":262144,"maxExplainCost":null,"maxConcurrentQueriesPerKey":4,
            "allowExplicitFeedbackContent":true}
            """;
        foreach (var expected in JsonDocument.Parse(resolved).RootElement.EnumerateObject())
        {
            json.TryGetProperty(expected.Name, out var actual).Should().BeTrue(expected.Name);
            actual.GetRawText().Should().Be(expected.Value.GetRawText(), expected.Name);
        }

        var stored = JsonDocument.Parse(resolved).RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetRawText());
        stored["maxRowLimit"] = "9000";
        stored["enforceReadOnly"] = "false";
        json.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo(stored.Keys.Concat(["stored", "lockedFields", "clampedFields"]));
        json.GetProperty("stored").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetRawText()).Should().BeEquivalentTo(stored);
        json.GetProperty("lockedFields").GetRawText().Should().Be("""["EnablePiiDetection","EnforceReadOnly"]""");
        json.GetProperty("clampedFields").GetRawText().Should().Be("""["MaxRowLimit"]""");
    }

    // The stored values the GET hands out are a copy: changing them changes nothing a later read returns.
    [Test]
    public async Task TheStoredSettings_AreACopy()
    {
        _globalRows.Add(new McpSettingsEntity { Id = 1, CustomPiiPatterns = "[\"iban\"]" });
        var provider = CreateProvider(NoLocks());

        var first = await provider.GetStoredSettingsAsync();
        first.CustomPiiPatterns.Add("changed");
        first.MaxRowLimit = 1;
        var second = await provider.GetStoredSettingsAsync();

        second.CustomPiiPatterns.Should().Equal("iban");
        second.MaxRowLimit.Should().Be(1000);
    }

    private static IEnumerable<string> GlobalOnlyFieldNames() => GlobalOnlyFields;

    private static IEnumerable<TestCaseData> LockCases()
    {
        yield return Case(new McpDeploymentOptions { ForceReadOnly = true, ForcePiiDetection = false }, "EnforceReadOnly", true);
        yield return Case(new McpDeploymentOptions { ForceReadOnly = false, ForcePiiDetection = true }, "EnablePiiDetection", true);
        yield return Case(Locked(x => x.ForceNoContentRetention = true), "RetainQueryContent", false);
        yield return Case(Locked(x => x.Audit = new McpAuditOptions { RequireContent = true }), "RetainQueryContent", true);
        yield return Case(Locked(x => x.ForbidRowDataToLlm = true), "EnableSampleValueCollection", false);
        yield return Case(Locked(x => x.ForbidRowDataToLlm = true), "EnableValueGrounding", false);
        yield return Case(Locked(x => x.ForbidRowDataToLlm = true), "EnableEvalJudge", false);

        static McpDeploymentOptions Locked(Action<McpDeploymentOptions> set)
        {
            var options = NoLocks();
            set(options);

            return options;
        }

        static TestCaseData Case(McpDeploymentOptions options, string field, bool pinned) =>
            new TestCaseData(options, field, pinned).SetArgDisplayNames(field, pinned ? "pinned on" : "pinned off");
    }

    private static McpDeploymentOptions NoLocks() => new() { ForceReadOnly = false, ForcePiiDetection = false };

    private static List<string> SettingNames(Type type, params string[] except)
    {
        return type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.CanWrite)
            .Select(x => x.Name)
            .Where(x => !except.Contains(x))
            .ToList();
    }

    // A valid value of the field's type that differs from its default (variant 0) and from variant 0 (variant 1).
    private static object Distinct(string field, int variant)
    {
        var defaults = Read(new McpSettingsData(), field);

        return Property(typeof(McpSettingsData), field).PropertyType switch
        {
            var x when x == typeof(bool) => variant == 0 ? !(bool)defaults! : (bool)defaults!,
            var x when x == typeof(int) => (int)defaults! + 1 + variant,
            var x when x == typeof(double) => 0.5 + (variant / 10.0),
            var x when x == typeof(decimal?) => 12.5m + variant,
            var x when x == typeof(string) => $"{field} {variant}",
            var x when x == typeof(List<string>) => new List<string> { $"pattern{variant}" },
            var x => throw new InvalidOperationException($"No test value for {x.Name} {field}.")
        };
    }

    private static object? Read(object settings, string field) => Property(settings.GetType(), field).GetValue(settings);

    private static void Write(object settings, string field, object? value) => Property(settings.GetType(), field).SetValue(settings, value);

    // A stored row keeps the custom PII patterns as JSON.
    private static object? RowValue(object row, string field)
    {
        var value = Read(row, field);

        return field == "CustomPiiPatterns"
            ? value is string json ? JsonSerializer.Deserialize<List<string>>(json) : new List<string>()
            : value;
    }

    private static void SetRow(object row, string field, object? value)
    {
        Write(row, field, value is List<string> patterns ? JsonSerializer.Serialize(patterns) : value);
    }

    private static PropertyInfo Property(Type type, string field) =>
        type.GetProperty(field, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"{type.Name} has no {field}.");

    private McpSettingsProvider CreateProvider(McpDeploymentOptions options)
    {
        return new McpSettingsProvider(CreateFactory(), _cache, Options.Create(options), NullLogger<McpSettingsProvider>.Instance);
    }

    private UpdateMcpSettingsHandler CreateGlobalHandler(McpDeploymentOptions options, IMcpSettingsProvider provider)
    {
        return new UpdateMcpSettingsHandler(CreateFactory(), provider, Options.Create(options), TestPermissions.Admin, TestProjectAccess.Actors(), new RecordingSecurityAudit(), NullLogger<UpdateMcpSettingsHandler>.Instance);
    }

    private UpdateMcpProjectSettingsHandler CreateProjectHandler(McpDeploymentOptions options, IMcpSettingsProvider provider)
    {
        return new UpdateMcpProjectSettingsHandler(CreateFactory(), provider, Options.Create(options), TestPermissions.Admin, TestProjectAccess.Actors(), new RecordingSecurityAudit(), NullLogger<UpdateMcpProjectSettingsHandler>.Instance);
    }

    private IDbContextFactory<BeaconContext> CreateFactory()
    {
        var projects = new List<Project> { new() { Id = ProjectId, Name = "warehouse" }, new() { Id = ProjectId + 1, Name = "other" } };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SettingsTablesContext(projects, _globalRows, _projectRows, _history));

        return factory.Object;
    }

    /// <summary>The settings tables over async-queryable doubles; Add captures into the lists — no DB (§4.7).</summary>
    private sealed class SettingsTablesContext(
        List<Project> projects,
        List<McpSettingsEntity> globalRows,
        List<McpProjectSettings> projectRows,
        List<AppSettingHistory> history) : BeaconContext(Options, "beacon")
    {
        private static readonly DbContextOptions<SettingsTablesContext> Options =
            new DbContextOptionsBuilder<SettingsTablesContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            object? set = typeof(TEntity) switch
            {
                var x when x == typeof(Project) => BuildSet(projects),
                var x when x == typeof(McpSettingsEntity) => BuildSet(globalRows),
                var x when x == typeof(McpProjectSettings) => BuildSet(projectRows),
                var x when x == typeof(AppSettingHistory) => BuildSet(history),
                _ => null
            };

            return set as DbSet<TEntity> ?? base.Set<TEntity>();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        private static DbSet<T> BuildSet<T>(List<T> data) where T : class
        {
            var queryable = data.AsQueryable();
            var set = new Mock<DbSet<T>>();
            set.As<IAsyncEnumerable<T>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new TestAsyncEnumerator<T>(data.GetEnumerator()));
            set.As<IQueryable<T>>()
                .Setup(x => x.Provider)
                .Returns(new TestAsyncQueryProvider<T>(queryable.Provider));
            set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(queryable.Expression);
            set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
            set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());
            set.Setup(x => x.Add(It.IsAny<T>())).Callback<T>(data.Add);

            return set.Object;
        }
    }
}
