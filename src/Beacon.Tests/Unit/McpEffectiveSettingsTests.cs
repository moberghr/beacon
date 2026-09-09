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
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using McpSettingsEntity = Beacon.Core.Data.Entities.McpSettings;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC1–SC3 of spec <c>mcp-project-settings</c>: the effective-settings resolution (deployment lock → project value
/// → global value → code default, clamped to ceilings), the change-token cache invalidation and the corrupt-JSON
/// fallback. The provider runs over async-queryable doubles (§4.7 — no DB) and a real <see cref="MemoryCache"/>.
/// </summary>
[TestFixture]
public class McpEffectiveSettingsTests
{
    private const int ProjectId = 42;

    private List<McpSettingsEntity> _globalRows = null!;
    private List<McpProjectSettings> _projectRows = null!;

    [SetUp]
    public void SetUp()
    {
        _globalRows = [];
        _projectRows = [];
    }

    // --- Resolve (pure) ---------------------------------------------------------------------------------------

    [Test]
    public void Resolve_NoRows_ReturnsCodeDefaults()
    {
        var result = McpSettingsProvider.Resolve(new McpSettingsData(), null, new McpDeploymentOptions());

        result.Effective.MaxRowLimit.Should().Be(1000);
        result.Effective.EnforceReadOnly.Should().BeTrue();
        result.Effective.RetainQueryContent.Should().BeTrue();
        result.Effective.StatementTimeoutSeconds.Should().Be(30);
        result.Effective.MaxResultBytes.Should().Be(262144);
        result.Effective.MaxExplainCost.Should().BeNull();
        result.Effective.MaxConcurrentQueriesPerKey.Should().Be(4);
        result.Effective.AllowExplicitFeedbackContent.Should().BeTrue();
        result.LockedFields.Should().BeEmpty();
        result.ClampedFields.Should().BeEmpty();
    }

    [Test]
    public void Resolve_ProjectValueOverridesGlobal_NullProjectFieldInherits()
    {
        var global = new McpSettingsData { MaxRowLimit = 500, EnablePiiDetection = false, StatementTimeoutSeconds = 45 };
        var project = new McpProjectSettingsData { MaxRowLimit = 200, EnablePiiDetection = null, CustomPiiPatterns = ["iban"] };

        var result = McpSettingsProvider.Resolve(global, project, new McpDeploymentOptions());

        result.Effective.MaxRowLimit.Should().Be(200, "project value wins");
        result.Effective.EnablePiiDetection.Should().BeFalse("null project field inherits the global value");
        result.Effective.StatementTimeoutSeconds.Should().Be(45, "untouched by the project row");
        result.Effective.CustomPiiPatterns.Should().Equal("iban");
    }

    [Test]
    public void Resolve_DoesNotMutateTheGlobalInput()
    {
        var global = new McpSettingsData { MaxRowLimit = 500, CustomPiiPatterns = ["a"] };
        var project = new McpProjectSettingsData { MaxRowLimit = 10, CustomPiiPatterns = ["b"] };

        McpSettingsProvider.Resolve(global, project, new McpDeploymentOptions());

        global.MaxRowLimit.Should().Be(500);
        global.CustomPiiPatterns.Should().Equal("a");
    }

    [Test]
    public void Resolve_Ceilings_LowerOnly_AndReportClampedFields()
    {
        var global = new McpSettingsData { MaxRowLimit = 800, MaxResultBytes = 1000 };
        var project = new McpProjectSettingsData { MaxRowLimit = 5000, StatementTimeoutSeconds = 10 };
        var options = new McpDeploymentOptions
        {
            Ceilings = new McpCeilingOptions { MaxRowLimit = 1000, StatementTimeoutSeconds = 60, MaxResultBytes = null }
        };

        var result = McpSettingsProvider.Resolve(global, project, options);

        result.Effective.MaxRowLimit.Should().Be(1000, "5000 exceeds the ceiling");
        result.Effective.StatementTimeoutSeconds.Should().Be(10, "below the ceiling — never raised");
        result.Effective.MaxResultBytes.Should().Be(1000, "null ceiling never clamps");
        result.ClampedFields.Should().Equal("MaxRowLimit");
    }

    [Test]
    public void Resolve_NullMaxExplainCost_MeansUnlimited_SoTheCeilingApplies()
    {
        var options = new McpDeploymentOptions { Ceilings = new McpCeilingOptions { MaxExplainCost = 250m } };

        var result = McpSettingsProvider.Resolve(new McpSettingsData(), null, options);

        result.Effective.MaxExplainCost.Should().Be(250m);
        result.ClampedFields.Should().Contain("MaxExplainCost");
    }

    [Test]
    public void Resolve_ForceReadOnly_PinsTrue_EvenWhenProjectAndGlobalSayFalse()
    {
        var global = new McpSettingsData { EnforceReadOnly = false };
        var project = new McpProjectSettingsData { EnforceReadOnly = false };

        var result = McpSettingsProvider.Resolve(global, project, new McpDeploymentOptions { ForceReadOnly = true });

        result.Effective.EnforceReadOnly.Should().BeTrue();
        result.LockedFields.Should().Equal("EnforceReadOnly");
    }

    [Test]
    public void Resolve_ForceNoContentRetention_PinsFalse()
    {
        var project = new McpProjectSettingsData { RetainQueryContent = true };

        var result = McpSettingsProvider.Resolve(new McpSettingsData(), project, new McpDeploymentOptions { ForceNoContentRetention = true });

        result.Effective.RetainQueryContent.Should().BeFalse();
        result.LockedFields.Should().Equal("RetainQueryContent");
        result.Effective.AllowExplicitFeedbackContent.Should().BeTrue("a separate project setting, not covered by the lock");
    }

    // --- Provider over the cache and the doubles -------------------------------------------------------------

    [Test]
    public async Task GetSettingsAsync_GlobalRow_AppliesLocksAndCeilings()
    {
        _globalRows.Add(new McpSettingsEntity { Id = 1, EnforceReadOnly = false, MaxRowLimit = 9000 });
        var provider = CreateProvider(new McpDeploymentOptions
        {
            ForceReadOnly = true,
            Ceilings = new McpCeilingOptions { MaxRowLimit = 2000 }
        });

        var settings = await provider.GetSettingsAsync();

        settings.EnforceReadOnly.Should().BeTrue();
        settings.MaxRowLimit.Should().Be(2000);
    }

    [Test]
    public async Task GetEffectiveSettingsAsync_ProjectRow_OverlaysGlobal()
    {
        _globalRows.Add(new McpSettingsEntity { Id = 1, MaxRowLimit = 500, LearningSignalRetentionDays = 90 });
        _projectRows.Add(new McpProjectSettings { Id = 7, ProjectId = ProjectId, MaxRowLimit = 50, RetainQueryContent = false });
        var provider = CreateProvider(new McpDeploymentOptions());

        var settings = await provider.GetEffectiveSettingsAsync(ProjectId);
        var other = await provider.GetEffectiveSettingsAsync(ProjectId + 1);

        settings.MaxRowLimit.Should().Be(50);
        settings.RetainQueryContent.Should().BeFalse();
        settings.LearningSignalRetentionDays.Should().Be(90, "inherited");
        other.MaxRowLimit.Should().Be(500, "a project with no row gets the global effective settings");
    }

    [Test]
    public async Task GetEffectiveSettingsAsync_ProjectIdZeroOrNegative_IsGlobal()
    {
        _globalRows.Add(new McpSettingsEntity { Id = 1, MaxRowLimit = 321 });
        var provider = CreateProvider(new McpDeploymentOptions());

        (await provider.GetEffectiveSettingsAsync(0)).MaxRowLimit.Should().Be(321);
        (await provider.GetEffectiveSettingsAsync(-5)).MaxRowLimit.Should().Be(321);
        (await provider.GetProjectOverridesAsync(0)).Should().BeNull();
    }

    [Test]
    public async Task GetEffectiveSettingsDetailAsync_ReportsOverridesLockedAndClamped()
    {
        _projectRows.Add(new McpProjectSettings { Id = 7, ProjectId = ProjectId, MaxRowLimit = 99999, EnforceReadOnly = false });
        var provider = CreateProvider(new McpDeploymentOptions
        {
            ForceReadOnly = true,
            Ceilings = new McpCeilingOptions { MaxRowLimit = 1000 }
        });

        var detail = await provider.GetEffectiveSettingsDetailAsync(ProjectId);
        var overrides = await provider.GetProjectOverridesAsync(ProjectId);

        detail.Effective.MaxRowLimit.Should().Be(1000);
        detail.Effective.EnforceReadOnly.Should().BeTrue();
        detail.LockedFields.Should().Equal("EnforceReadOnly");
        detail.ClampedFields.Should().Equal("MaxRowLimit");
        overrides.Should().NotBeNull();
        overrides!.MaxRowLimit.Should().Be(99999, "the stored override is reported raw; clamping is a resolution concern");
        overrides.EnforceReadOnly.Should().BeFalse();
    }

    [Test]
    public async Task CorruptProjectPiiJson_FallsBackToGlobalPatterns_AndKeepsOtherOverrides()
    {
        _globalRows.Add(new McpSettingsEntity { Id = 1, CustomPiiPatterns = "[\"iban\"]" });
        _projectRows.Add(new McpProjectSettings { Id = 7, ProjectId = ProjectId, CustomPiiPatterns = "{not json", MaxRowLimit = 5 });
        var provider = CreateProvider(new McpDeploymentOptions());

        var settings = await provider.GetEffectiveSettingsAsync(ProjectId);

        settings.CustomPiiPatterns.Should().Equal(new[] { "iban" }, "corrupt project JSON inherits the global list rather than dropping protection");
        settings.MaxRowLimit.Should().Be(5);
    }

    [Test]
    public async Task InvalidateCache_ExpiresGlobalAndProjectEntries()
    {
        _globalRows.Add(new McpSettingsEntity { Id = 1, MaxRowLimit = 100 });
        _projectRows.Add(new McpProjectSettings { Id = 7, ProjectId = ProjectId, MaxRowLimit = 10 });
        var provider = CreateProvider(new McpDeploymentOptions());

        (await provider.GetSettingsAsync()).MaxRowLimit.Should().Be(100);
        (await provider.GetEffectiveSettingsAsync(ProjectId)).MaxRowLimit.Should().Be(10);

        _globalRows[0].MaxRowLimit = 200;
        _projectRows[0].MaxRowLimit = 20;

        (await provider.GetSettingsAsync()).MaxRowLimit.Should().Be(100, "still cached");
        (await provider.GetEffectiveSettingsAsync(ProjectId)).MaxRowLimit.Should().Be(10, "still cached");

        provider.InvalidateCache();

        (await provider.GetSettingsAsync()).MaxRowLimit.Should().Be(200);
        (await provider.GetEffectiveSettingsAsync(ProjectId)).MaxRowLimit.Should().Be(20);
    }

    [Test]
    public async Task InvalidateCache_FromAnotherInstance_ExpiresThisInstancesEntries()
    {
        // The provider is transient: the reset token must be shared across instances (static), or a write on
        // request A would leave request B's cached copy stale for five minutes.
        _globalRows.Add(new McpSettingsEntity { Id = 1, MaxRowLimit = 100 });
        var cache = new MemoryCache(new MemoryCacheOptions());
        var reader = CreateProvider(new McpDeploymentOptions(), cache);
        var writer = CreateProvider(new McpDeploymentOptions(), cache);

        (await reader.GetSettingsAsync()).MaxRowLimit.Should().Be(100);
        _globalRows[0].MaxRowLimit = 300;
        writer.InvalidateCache();

        (await reader.GetSettingsAsync()).MaxRowLimit.Should().Be(300);
    }

    [Test]
    public void Validator_RejectsNonPositiveCeilings()
    {
        var validator = new McpDeploymentOptionsValidator();

        var bad = validator.Validate(null, new McpDeploymentOptions { Ceilings = new McpCeilingOptions { MaxRowLimit = 0, MaxExplainCost = -1m } });
        var good = validator.Validate(null, new McpDeploymentOptions { Ceilings = new McpCeilingOptions { MaxRowLimit = 10 } });
        var absent = validator.Validate(null, new McpDeploymentOptions());

        bad.Failed.Should().BeTrue();
        bad.Failures.Should().HaveCount(2);
        good.Succeeded.Should().BeTrue();
        absent.Succeeded.Should().BeTrue();
    }

    private McpSettingsProvider CreateProvider(McpDeploymentOptions options, IMemoryCache? cache = null)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SettingsTestContext(_globalRows, _projectRows));

        return new McpSettingsProvider(
            factory.Object,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            Options.Create(options),
            NullLogger<McpSettingsProvider>.Instance);
    }

    /// <summary>Serves the two settings tables over async-queryable doubles — no DB (§4.7).</summary>
    private sealed class SettingsTestContext(List<McpSettingsEntity> globalRows, List<McpProjectSettings> projectRows) : BeaconContext(Options, "beacon")
    {
        private static readonly DbContextOptions<SettingsTestContext> Options =
            new DbContextOptionsBuilder<SettingsTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpSettingsEntity))
            {
                return (DbSet<TEntity>)(object)BuildSet(globalRows);
            }

            if (typeof(TEntity) == typeof(McpProjectSettings))
            {
                return (DbSet<TEntity>)(object)BuildSet(projectRows);
            }

            return base.Set<TEntity>();
        }

        private static DbSet<T> BuildSet<T>(List<T> data) where T : class
        {
            var queryable = data.AsQueryable();
            var set = new Mock<DbSet<T>>();
            set.As<IAsyncEnumerable<T>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new TestAsyncEnumerator<T>(queryable.GetEnumerator()));
            set.As<IQueryable<T>>()
                .Setup(x => x.Provider)
                .Returns(new TestAsyncQueryProvider<T>(queryable.Provider));
            set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(queryable.Expression);
            set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
            set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => queryable.GetEnumerator());
            return set.Object;
        }
    }

    // A-F001: deployment locks are ONE table (McpLockPolicy) that resolution and both write handlers iterate.
    [Test]
    public void LockPolicy_NoLocksConfigured_HasNoActiveRules()
    {
        McpLockPolicy.ActiveRules(new McpDeploymentOptions()).Should().BeEmpty();
    }

    [Test]
    public void LockPolicy_BothLocks_PinContradictDetectOverrides_AndKeepStored()
    {
        var rules = McpLockPolicy.ActiveRules(new McpDeploymentOptions { ForceReadOnly = true, ForceNoContentRetention = true });

        rules.Select(x => x.FieldName).Should().BeEquivalentTo("EnforceReadOnly", "RetainQueryContent");
        rules.Select(x => x.LockName).Should().BeEquivalentTo("ForceReadOnly", "ForceNoContentRetention");

        var effective = new McpSettingsData { EnforceReadOnly = false, RetainQueryContent = true };
        foreach (var rule in rules)
        {
            rule.Pin(effective);
        }

        effective.EnforceReadOnly.Should().BeTrue();
        effective.RetainQueryContent.Should().BeFalse();

        rules.Count(x => x.Contradicts(new McpSettingsData { EnforceReadOnly = false, RetainQueryContent = true })).Should().Be(2);
        rules.Count(x => x.Contradicts(new McpSettingsData { EnforceReadOnly = true, RetainQueryContent = false })).Should().Be(0);
        rules.Count(x => x.HasOverride(new McpProjectSettingsData { EnforceReadOnly = true })).Should().Be(1, "any non-null override counts, even one agreeing with the lock");
        rules.Count(x => x.HasOverride(new McpProjectSettingsData())).Should().Be(0, "null = inherit is never an override");

        var incoming = new McpSettingsData { EnforceReadOnly = true, RetainQueryContent = false };
        var stored = new McpSettingsEntity { EnforceReadOnly = false, RetainQueryContent = true };
        foreach (var rule in rules)
        {
            rule.KeepStored(incoming, stored);
        }

        incoming.EnforceReadOnly.Should().BeFalse("the stored admin value survives a locked-field echo");
        incoming.RetainQueryContent.Should().BeTrue();
    }

    [Test]
    public void LockPolicy_OneLock_OnlyThatRuleIsActive()
    {
        var rules = McpLockPolicy.ActiveRules(new McpDeploymentOptions { ForceNoContentRetention = true });

        rules.Should().ContainSingle().Which.FieldName.Should().Be("RetainQueryContent");
    }
}
