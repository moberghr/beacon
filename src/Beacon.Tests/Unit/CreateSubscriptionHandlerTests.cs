using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.Services;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The React create dialog sends the full subscription setup over HTTP; every field has to reach
/// <c>ISubscriptionService.CreateSubscription</c>, or the subscription silently runs with defaults
/// (no attachment format means no attachment, no parameters means a parameterized query is rejected).
/// </summary>
[TestFixture]
public class CreateSubscriptionHandlerTests
{
    [Test]
    public async Task Handle_MapsEveryFieldOntoSubscriptionData()
    {
        var captured = await HandleCapturing(
            new CreateSubscriptionCommand(
                QueryId: 12,
                CronExpression: "0 9 * * *",
                RecipientIds: [7, 8],
                MaxRows: 50,
                TimeoutSeconds: 120,
                IncludeAttachment: true,
                ShowQuery: true,
                StoreResults: true,
                CreateTasks: false,
                NotificationTrigger: NotificationTrigger.OnResultCountIncrease,
                MinimumRowCount: 10,
                ResultAttachmentType: FileType.Xlsx,
                Parameters: [new CreateSubscriptionParameter("{from}", "2026-01-01")],
                AnomalyConfig: new CreateSubscriptionAnomalyConfig(
                    AnomalyDetectionMethod.IQR,
                    AnomalySensitivity.High,
                    LookbackDays: 60,
                    MinimumDataPoints: 14,
                    AlertOnIncrease: true,
                    AlertOnDecrease: false)));

        captured.QueryId.Should().Be(12);
        captured.CronExpression.Should().Be("0 9 * * *");
        captured.Recipients.Select(x => x.RecipientId).Should().Equal(7, 8);
        captured.MaxRows.Should().Be(50);
        captured.TimeoutSeconds.Should().Be(120);
        captured.IncludeAttachment.Should().BeTrue();
        captured.ShowQuery.Should().BeTrue();
        captured.StoreResults.Should().BeTrue();
        captured.CreateTasks.Should().BeFalse();
        captured.NotificationTrigger.Should().Be(NotificationTrigger.OnResultCountIncrease);
        captured.MinimumRowCount.Should().Be(10);
        captured.ResultAttachmentType.Should().Be(FileType.Xlsx);
        captured.Parameters.Should().ContainSingle();
        captured.Parameters[0].QueryPlaceholder.Should().Be("{from}");
        captured.Parameters[0].Value.Should().Be("2026-01-01");
        captured.AnomalyConfig.Should().BeEquivalentTo(new
        {
            Enabled = true,
            DetectionMethod = AnomalyDetectionMethod.IQR,
            Sensitivity = AnomalySensitivity.High,
            LookbackDays = 60,
            MinimumDataPoints = 14,
            AlertOnIncrease = true,
            AlertOnDecrease = false,
        });
    }

    [Test]
    public async Task Handle_NoAnomalyConfigOrParameters_LeavesDetectionOffAndParametersEmpty()
    {
        var captured = await HandleCapturing(
            new CreateSubscriptionCommand(
                QueryId: 12,
                CronExpression: "0 9 * * *",
                RecipientIds: [7],
                MaxRows: null,
                TimeoutSeconds: null,
                IncludeAttachment: false,
                ShowQuery: false,
                StoreResults: false,
                CreateTasks: false,
                NotificationTrigger: NotificationTrigger.Always,
                MinimumRowCount: null,
                ResultAttachmentType: null,
                Parameters: null,
                AnomalyConfig: null));

        captured.AnomalyConfig.Should().BeNull();
        captured.Parameters.Should().BeEmpty();
        captured.NotificationTrigger.Should().Be(NotificationTrigger.Always);
        captured.ResultAttachmentType.Should().BeNull();
    }

    private static async Task<SubscriptionData> HandleCapturing(CreateSubscriptionCommand command)
    {
        SubscriptionData? captured = null;
        var service = new Mock<ISubscriptionService>();
        service
            .Setup(x => x.CreateSubscription(It.IsAny<SubscriptionData>(), It.IsAny<CancellationToken>()))
            .Callback<SubscriptionData, CancellationToken>((data, _) => captured = data)
            .ReturnsAsync(new BaseResponse { Success = true, Message = "Subscription created successfully" });
        var handler = new CreateSubscriptionHandler(service.Object);

        await handler.Handle(command, CancellationToken.None);

        captured.Should().NotBeNull();
        return captured!;
    }
}
