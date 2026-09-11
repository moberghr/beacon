using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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

namespace Beacon.Tests.Unit;

/// <summary>
/// SC4 (handler half) and SC5 of spec <c>mcp-project-settings</c>: the per-project update handler upserts one row,
/// keeps null overrides null, refuses writes that contradict a deployment lock with <see cref="SettingLockedException"/>,
/// validates custom PII regexes and unknown projects, and invalidates the provider cache once per write. The read
/// handler composes overrides, effective values and lock/clamp names. Async-queryable doubles, no DB (§4.7).
/// </summary>
[TestFixture]
public class McpProjectSettingsHandlerTests
{
    private const int ProjectId = 42;

    private List<Project> _projects = null!;
    private List<McpProjectSettings> _rows = null!;
    private Mock<IMcpSettingsProvider> _provider = null!;
    private int _saveCalls;

    [SetUp]
    public void SetUp()
    {
        _projects = [new Project { Id = ProjectId, Name = "warehouse" }];
        _rows = [];
        _provider = SettingsProviderMock.Create(new McpSettingsData { MaxRowLimit = 500 });
        _saveCalls = 0;
    }

    [Test]
    public async Task Update_FirstWrite_CreatesRow_NullsStayNull_AndInvalidates()
    {
        var data = new McpProjectSettingsData { MaxRowLimit = 50, RetainQueryContent = false, CustomPiiPatterns = ["iban"] };

        await CreateUpdateHandler().Handle(new UpdateMcpProjectSettingsCommand(ProjectId, data), CancellationToken.None);

        _rows.Should().ContainSingle();
        _rows[0].ProjectId.Should().Be(ProjectId);
        _rows[0].MaxRowLimit.Should().Be(50);
        _rows[0].RetainQueryContent.Should().BeFalse();
        _rows[0].CustomPiiPatterns.Should().Be("[\"iban\"]");
        _rows[0].EnforceReadOnly.Should().BeNull("not overridden → inherit");
        _rows[0].StatementTimeoutSeconds.Should().BeNull();
        _saveCalls.Should().Be(1);
        _provider.Verify(x => x.InvalidateCache(), Times.Once);
    }

    [Test]
    public async Task Update_SecondWrite_UpdatesTheExistingRow()
    {
        _rows.Add(new McpProjectSettings { Id = 7, ProjectId = ProjectId, MaxRowLimit = 50, EnablePiiDetection = false });

        await CreateUpdateHandler().Handle(
            new UpdateMcpProjectSettingsCommand(ProjectId, new McpProjectSettingsData { MaxRowLimit = 75 }),
            CancellationToken.None);

        _rows.Should().ContainSingle("upsert, never a second row");
        _rows[0].MaxRowLimit.Should().Be(75);
        _rows[0].EnablePiiDetection.Should().BeNull("a null in the request clears the override back to inherit");
    }

    [Test]
    public async Task Update_LockedField_ThrowsSettingLocked_AndWritesNothing()
    {
        var handler = CreateUpdateHandler(new McpDeploymentOptions { ForceReadOnly = true });

        var act = () => handler.Handle(
            new UpdateMcpProjectSettingsCommand(ProjectId, new McpProjectSettingsData { EnforceReadOnly = false, MaxRowLimit = 10 }),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<SettingLockedException>();
        ex.Which.FieldName.Should().Be("EnforceReadOnly");
        _rows.Should().BeEmpty("no field of a refused request is persisted");
        _saveCalls.Should().Be(0);
        _provider.Verify(x => x.InvalidateCache(), Times.Never);
    }

    [Test]
    public async Task Update_NullOverrideOnLockedField_IsAllowed()
    {
        // null = inherit; the inherited value is pinned by the lock, so there is nothing to refuse.
        var handler = CreateUpdateHandler(new McpDeploymentOptions { ForceNoContentRetention = true });

        await handler.Handle(
            new UpdateMcpProjectSettingsCommand(ProjectId, new McpProjectSettingsData { RetainQueryContent = null, MaxRowLimit = 10 }),
            CancellationToken.None);

        _rows.Should().ContainSingle();
    }

    [Test]
    public async Task Update_RetainQueryContentTrue_UnderNoRetentionLock_Throws()
    {
        var handler = CreateUpdateHandler(new McpDeploymentOptions { ForceNoContentRetention = true });

        var act = () => handler.Handle(
            new UpdateMcpProjectSettingsCommand(ProjectId, new McpProjectSettingsData { RetainQueryContent = true }),
            CancellationToken.None);

        (await act.Should().ThrowAsync<SettingLockedException>()).Which.FieldName.Should().Be("RetainQueryContent");
    }

    [Test]
    public async Task Update_EmptyPiiPatternList_IsStoredAsEmptyArray_NotAsInherit()
    {
        // Review finding F009: null = inherit; a non-null EMPTY list is a real override ("no custom patterns for
        // this project") and must round-trip as "[]", or the UI toggle turns itself off on the next GET.
        await CreateUpdateHandler().Handle(
            new UpdateMcpProjectSettingsCommand(ProjectId, new McpProjectSettingsData { CustomPiiPatterns = [] }),
            CancellationToken.None);

        _rows.Should().ContainSingle();
        _rows[0].CustomPiiPatterns.Should().Be("[]");
    }

    [Test]
    public async Task Update_AgreeingOverrideOnLockedField_IsStillRefused()
    {
        // Review finding F005 / spec "Unwanted behaviours": ANY override of a locked field is refused, even one
        // that agrees with the lock today — it would silently take effect the day the lock is lifted.
        var handler = CreateUpdateHandler(new McpDeploymentOptions { ForceReadOnly = true });

        var act = () => handler.Handle(
            new UpdateMcpProjectSettingsCommand(ProjectId, new McpProjectSettingsData { EnforceReadOnly = true }),
            CancellationToken.None);

        (await act.Should().ThrowAsync<SettingLockedException>()).Which.FieldName.Should().Be("EnforceReadOnly");
        _rows.Should().BeEmpty();
        _saveCalls.Should().Be(0);
    }

    [Test]
    public async Task Update_InvalidPiiRegex_IsRejectedBeforeAnyWrite()
    {
        var act = () => CreateUpdateHandler().Handle(
            new UpdateMcpProjectSettingsCommand(ProjectId, new McpProjectSettingsData { CustomPiiPatterns = ["(unclosed"] }),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a valid regular expression*");
        _rows.Should().BeEmpty();
    }

    [Test]
    public async Task Update_UnknownProject_Throws()
    {
        var act = () => CreateUpdateHandler().Handle(
            new UpdateMcpProjectSettingsCommand(999, new McpProjectSettingsData { MaxRowLimit = 1 }),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Project 999 not found.");
    }

    [Test]
    public async Task Get_ComposesOverridesEffectiveAndLockNames()
    {
        var provider = SettingsProviderMock.Create(
            new McpSettingsData { MaxRowLimit = 1000, EnforceReadOnly = true },
            overrides: new McpProjectSettingsData { MaxRowLimit = 5000 },
            lockedFields: ["EnforceReadOnly"],
            clampedFields: ["MaxRowLimit"]);

        var result = await new GetMcpProjectSettingsHandler(CreateFactory(), provider.Object)
            .Handle(new GetMcpProjectSettingsQuery(ProjectId), CancellationToken.None);

        result.ProjectId.Should().Be(ProjectId);
        result.Overrides.MaxRowLimit.Should().Be(5000);
        result.Effective.MaxRowLimit.Should().Be(1000);
        result.LockedFields.Should().Equal("EnforceReadOnly");
        result.ClampedFields.Should().Equal("MaxRowLimit");
        provider.Verify(x => x.GetEffectiveSettingsDetailAsync(ProjectId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Get_NoRow_ReturnsEmptyOverrides()
    {
        var result = await new GetMcpProjectSettingsHandler(CreateFactory(), _provider.Object)
            .Handle(new GetMcpProjectSettingsQuery(ProjectId), CancellationToken.None);

        result.Overrides.Should().NotBeNull();
        result.Overrides.MaxRowLimit.Should().BeNull();
    }

    [Test]
    public async Task Get_UnknownProject_Throws()
    {
        var act = () => new GetMcpProjectSettingsHandler(CreateFactory(), _provider.Object)
            .Handle(new GetMcpProjectSettingsQuery(999), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Project 999 not found.");
    }

    private UpdateMcpProjectSettingsHandler CreateUpdateHandler(McpDeploymentOptions? options = null)
    {
        return new UpdateMcpProjectSettingsHandler(CreateFactory(), _provider.Object, Options.Create(options ?? new McpDeploymentOptions()));
    }

    private IDbContextFactory<BeaconContext> CreateFactory()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ProjectSettingsTestContext(_projects, _rows, () => _saveCalls++));

        return factory.Object;
    }

    /// <summary>Projects + McpProjectSettings over async-queryable doubles; Add captures into the row list.</summary>
    private sealed class ProjectSettingsTestContext : BeaconContext
    {
        private static readonly DbContextOptions<ProjectSettingsTestContext> Options =
            new DbContextOptionsBuilder<ProjectSettingsTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly List<Project> _projects;
        private readonly List<McpProjectSettings> _rows;
        private readonly Action _onSave;

        public ProjectSettingsTestContext(List<Project> projects, List<McpProjectSettings> rows, Action onSave) : base(Options, "beacon")
        {
            _projects = projects;
            _rows = rows;
            _onSave = onSave;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(Project))
            {
                return (DbSet<TEntity>)(object)BuildSet(_projects, null);
            }

            if (typeof(TEntity) == typeof(McpProjectSettings))
            {
                return (DbSet<TEntity>)(object)BuildSet(_rows, x => _rows.Add(x));
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges()
        {
            _onSave();
            return 0;
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            _onSave();
            return Task.FromResult(0);
        }

        private static DbSet<T> BuildSet<T>(List<T> data, Action<T>? onAdd) where T : class
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
            if (onAdd != null)
            {
                set.Setup(x => x.Add(It.IsAny<T>())).Callback<T>(onAdd);
            }

            return set.Object;
        }
    }
}
