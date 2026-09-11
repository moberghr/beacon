using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.McpSettings;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

[TestFixture]
public class UpdateMcpSettingsHandlerTests
{
    private static UpdateMcpSettingsHandler CreateHandler(McpDeploymentOptions? options = null)
    {
        // The invalid-regex and deployment-lock guards run before any DB access, so these doubles are never
        // invoked on the rejection path — a throwing factory proves the validation short-circuits first.
        var factory = new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict);
        var provider = new Mock<IMcpSettingsProvider>(MockBehavior.Strict);
        return new UpdateMcpSettingsHandler(factory.Object, provider.Object, Options.Create(options ?? new McpDeploymentOptions()));
    }

    [TestCase("(unclosed")]
    [TestCase("a{2,1}")]
    [TestCase("*invalid")]
    public async Task Handle_InvalidCustomPiiPattern_Throws(string badPattern)
    {
        var handler = CreateHandler();
        var command = new UpdateMcpSettingsCommand(new McpSettingsData
        {
            CustomPiiPatterns = [badPattern],
        });

        var act = async () => await handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*valid regular expression*");
    }

    // SC4 (global half of spec mcp-project-settings): the global PUT refuses a write that contradicts a
    // deployment lock with SettingLockedException (→ 409) and touches nothing — the strict factory throws
    // on any DB access.
    [Test]
    public async Task Handle_EnforceReadOnlyFalse_UnderForceReadOnly_ThrowsSettingLocked_AndWritesNothing()
    {
        var handler = CreateHandler(new McpDeploymentOptions { ForceReadOnly = true });

        var act = () => handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { EnforceReadOnly = false }),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<SettingLockedException>();
        ex.Which.FieldName.Should().Be("EnforceReadOnly");
        ex.Which.Message.Should().Contain("ForceReadOnly");
    }

    [Test]
    public async Task Handle_RetainQueryContentTrue_UnderForceNoContentRetention_ThrowsSettingLocked_AndWritesNothing()
    {
        var handler = CreateHandler(new McpDeploymentOptions { ForceNoContentRetention = true });

        var act = () => handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { RetainQueryContent = true }),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<SettingLockedException>();
        ex.Which.FieldName.Should().Be("RetainQueryContent");
    }

    // Review finding F001: the global GET returns lock/ceiling-RESOLVED values and the page re-sends every
    // field, so a locked or clamped value echoed back unchanged must NOT overwrite the stored value —
    // otherwise lifting the lock/ceiling later would no longer restore the admin's configuration.
    [Test]
    public async Task Handle_ClampedValueEchoedBack_KeepsStoredValue_ButDeliberateEditIsWritten()
    {
        var stored = new McpSettings { MaxRowLimit = 9000, StatementTimeoutSeconds = 120, MaxExplainCost = null };
        var (handler, saves) = CreateHandlerWithRow(
            stored,
            new McpDeploymentOptions
            {
                Ceilings = new McpCeilingOptions { MaxRowLimit = 1000, StatementTimeoutSeconds = 60, MaxExplainCost = 50m }
            });

        // The page showed the resolved values 1000 / 60 / 50 and sends them back; the admin only edited the timeout.
        await handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { MaxRowLimit = 1000, StatementTimeoutSeconds = 30, MaxExplainCost = 50m }),
            CancellationToken.None);

        stored.MaxRowLimit.Should().Be(9000, "echoed ceiling value is not an edit");
        stored.MaxExplainCost.Should().BeNull("echoed ceiling value is not an edit (null = no limit stays stored)");
        stored.StatementTimeoutSeconds.Should().Be(30, "a value other than the resolved one is a deliberate edit");
        saves().Should().Be(1);
    }

    [Test]
    public async Task Handle_LockedFieldEchoedBack_KeepsStoredValue()
    {
        var stored = new McpSettings { EnforceReadOnly = false, RetainQueryContent = true };
        var (handler, _) = CreateHandlerWithRow(
            stored,
            new McpDeploymentOptions { ForceReadOnly = true, ForceNoContentRetention = true });

        // Under both locks the page can only send the pinned values (anything else is refused with 409).
        await handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { EnforceReadOnly = true, RetainQueryContent = false, MaxRowLimit = 5 }),
            CancellationToken.None);

        stored.EnforceReadOnly.Should().BeFalse("the lock pins the effective value; the stored admin value survives it");
        stored.RetainQueryContent.Should().BeTrue();
        stored.MaxRowLimit.Should().Be(5, "unlocked fields are written normally");
    }

    [Test]
    public async Task Handle_NoCeilingOrLock_WritesEveryFieldAsSent()
    {
        var stored = new McpSettings { MaxRowLimit = 9000, EnforceReadOnly = false };
        var (handler, _) = CreateHandlerWithRow(stored, new McpDeploymentOptions());

        await handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { MaxRowLimit = 1000, EnforceReadOnly = true }),
            CancellationToken.None);

        stored.MaxRowLimit.Should().Be(1000);
        stored.EnforceReadOnly.Should().BeTrue();
    }

    private static (UpdateMcpSettingsHandler Handler, Func<int> Saves) CreateHandlerWithRow(McpSettings row, McpDeploymentOptions options)
    {
        var saves = 0;
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SettingsRowTestContext([row], () => saves++));

        var provider = SettingsProviderMock.Create();

        return (new UpdateMcpSettingsHandler(factory.Object, provider.Object, Options.Create(options)), () => saves);
    }

    /// <summary>The single McpSettings row over async-queryable doubles; no DB (§4.7).</summary>
    private sealed class SettingsRowTestContext : BeaconContext
    {
        private static readonly DbContextOptions<SettingsRowTestContext> Options =
            new DbContextOptionsBuilder<SettingsRowTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly List<McpSettings> _rows;
        private readonly Action _onSave;

        public SettingsRowTestContext(List<McpSettings> rows, Action onSave) : base(Options, "beacon")
        {
            _rows = rows;
            _onSave = onSave;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpSettings))
            {
                var queryable = _rows.AsQueryable();
                var set = new Mock<DbSet<McpSettings>>();
                set.As<IAsyncEnumerable<McpSettings>>()
                    .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                    .Returns(() => new TestAsyncEnumerator<McpSettings>(queryable.GetEnumerator()));
                set.As<IQueryable<McpSettings>>()
                    .Setup(x => x.Provider)
                    .Returns(new TestAsyncQueryProvider<McpSettings>(queryable.Provider));
                set.As<IQueryable<McpSettings>>().Setup(x => x.Expression).Returns(queryable.Expression);
                set.As<IQueryable<McpSettings>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
                set.As<IQueryable<McpSettings>>().Setup(x => x.GetEnumerator()).Returns(() => queryable.GetEnumerator());
                return (DbSet<TEntity>)(object)set.Object;
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
    }
}
