using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Core.Services.Retention;

namespace Beacon.Tests.Unit;

[TestFixture]
public class MaintenanceHeartbeatRecorderTests
{
    [TestCase(MaintenanceJobNames.McpAuditRetention)]
    [TestCase(MaintenanceJobNames.SecurityAuditRetention)]
    [TestCase(MaintenanceJobNames.SecurityAuditVerify)]
    public async Task RecordAsync_CancelledDuringTheWrite_ReturnsInsteadOfThrowing(string job)
    {
        using var cancellation = new CancellationTokenSource();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });
        var recorder = new MaintenanceHeartbeatRecorder(factory.Object, TimeProvider.System, NullLogger<MaintenanceHeartbeatRecorder>.Instance);

        var act = () => recorder.RecordAsync(job, cancellation.Token);

        await act.Should().NotThrowAsync();
        factory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Once);
        cancellation.IsCancellationRequested.Should().BeTrue();
    }
}
