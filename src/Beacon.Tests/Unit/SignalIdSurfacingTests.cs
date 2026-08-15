using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.MCP.Services;

namespace Beacon.Tests.Unit;

/// <summary>
/// Part B — the signal-id surfacing contract at its substantive seam: <see cref="McpSignalService"/>
/// .RecordSignalAsync now returns the persisted signal id (or null), which <c>ProjectAskTool</c> appends
/// to its data-query response as <c>_signal_id: N_</c> for the <c>feedback</c> tool to target. The append
/// itself is a trivial 3-line glue over this return value; the meaningful new behavior — returning the id
/// when learning is on and null when off — is exercised here against a mocked context (no DB, §4.7).
/// </summary>
[TestFixture]
public class SignalIdSurfacingTests
{
    [Test]
    public async Task RecordSignalAsync_WhenLearningEnabled_ReturnsPersistedSignalId()
    {
        const int assignedId = 812;
        var signal = new McpQuerySignal { Tool = "ask", Question = "how many orders?" };

        var context = new SignalContext(assignedId);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(context);

        var settings = new Mock<IMcpSettingsProvider>();
        settings.Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData { EnableLearning = true });

        var service = new McpSignalService(factory.Object, settings.Object, NullLogger<McpSignalService>.Instance);

        var result = await service.RecordSignalAsync(signal, CancellationToken.None);

        result.Should().Be(assignedId, "the persisted signal's DB-assigned id is returned for the ask tool to surface");
        context.Added.Should().ContainSingle().Which.Should().BeSameAs(signal);
        context.SaveCount.Should().Be(1);
    }

    [Test]
    public async Task RecordSignalAsync_WhenLearningDisabled_ReturnsNullAndPersistsNothing()
    {
        var signal = new McpQuerySignal { Tool = "ask", Question = "how many orders?" };

        // A strict factory proves the DB is never touched when learning is off (the method early-returns).
        var factory = new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict);

        var settings = new Mock<IMcpSettingsProvider>();
        settings.Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData { EnableLearning = false });

        var service = new McpSignalService(factory.Object, settings.Object, NullLogger<McpSignalService>.Instance);

        var result = await service.RecordSignalAsync(signal, CancellationToken.None);

        result.Should().BeNull("no signal is persisted when learning is disabled, so there is no id to surface");
        factory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A <see cref="BeaconContext"/> whose <c>McpQuerySignals</c> set records Add calls and assigns the
    /// given id on Add (simulating the DB identity column), so RecordSignalAsync can read signal.Id after save.
    /// </summary>
    private sealed class SignalContext : BeaconContext
    {
        private static readonly DbContextOptions<SignalContext> Options =
            new DbContextOptionsBuilder<SignalContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly int _assignedId;
        private readonly Mock<DbSet<McpQuerySignal>> _set = new();

        public List<McpQuerySignal> Added { get; } = [];
        public int SaveCount { get; private set; }

        public SignalContext(int assignedId) : base(Options, "beacon")
        {
            _assignedId = assignedId;
            _set.Setup(x => x.Add(It.IsAny<McpQuerySignal>()))
                .Callback<McpQuerySignal>(x =>
                {
                    x.Id = _assignedId;
                    Added.Add(x);
                });
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpQuerySignal))
            {
                return (DbSet<TEntity>)(object)_set.Object;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => ++SaveCount;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(1);
        }
    }
}
