using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Beacon.Api.Endpoints;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Recipients;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Services;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// Who may change where data is delivered: every mutating subscription route and every data-contract write needs the
/// Execute scope (on top of the write permission the group's permission filter enforces), so a Read-scoped key can
/// read subscriptions but not create, re-route, run or re-activate them.
/// </summary>
[TestFixture]
public class NotificationEndpointAuthorizationTests
{
    [TestCase("CreateSubscription")]
    [TestCase("DeleteSubscription")]
    [TestCase("ReactivateSubscription")]
    [TestCase("SetSubscriptionSla")]
    [TestCase("TestSubscription")]
    [TestCase("AddSubscriptionRecipients")]
    [TestCase("RemoveSubscriptionRecipient")]
    public async Task MutatingSubscriptionRoutes_RequireTheExecuteScope(string endpointName)
    {
        await using var app = await NotificationTestKit.StartApiAsync(Mock.Of<IMediator>(), NotificationTestKit.ReadScopedApiKey(), x => x.MapSubscriptionsEndpoints());

        Policies(app.Services, endpointName).Should().Contain(BeaconApiEndpoints.ExecuteScopePolicyName);
    }

    [TestCase("CreateDataContract")]
    [TestCase("UpdateDataContract")]
    [TestCase("DeleteDataContract")]
    [TestCase("EvaluateDataContract")]
    public async Task DataContractWrites_RequireTheExecuteScope(string endpointName)
    {
        await using var app = await NotificationTestKit.StartApiAsync(Mock.Of<IMediator>(), NotificationTestKit.ReadScopedApiKey(), x => x.MapDataQualityEndpoints());

        Policies(app.Services, endpointName).Should().Contain(BeaconApiEndpoints.ExecuteScopePolicyName);
    }

    [Test]
    public async Task ReadScopedApiKey_CanReadASubscriptionButNotRouteOrRunIt()
    {
        var mediator = new Mock<IMediator>();
        await using var app = await NotificationTestKit.StartApiAsync(mediator.Object, NotificationTestKit.ReadScopedApiKey(), x => x.MapSubscriptionsEndpoints());
        var client = app.GetTestClient();

        var read = await client.GetAsync("/beacon/api/subscriptions/5");
        var attach = await client.PostAsJsonAsync("/beacon/api/subscriptions/5/recipients", new { recipientIds = new[] { 9 } });
        var run = await client.PostAsync("/beacon/api/subscriptions/5/execute", content: null);
        var create = await client.PostAsJsonAsync("/beacon/api/subscriptions", new { queryId = 1, cronExpression = "0 * * * *", recipientIds = new[] { 9 } });

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        attach.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        run.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        mediator.Verify(x => x.Send(It.IsAny<AddSubscriptionRecipientsCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        mediator.Verify(x => x.Send(It.IsAny<TestSubscriptionCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        mediator.Verify(x => x.Send(It.IsAny<CreateSubscriptionCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ExecuteScopedApiKey_CanAttachAnExistingRecipient()
    {
        var mediator = new Mock<IMediator>();
        var executeKey = NotificationTestKit.ReadScopedApiKey();
        ((System.Security.Claims.ClaimsIdentity)executeKey.Identity!).AddClaim(new System.Security.Claims.Claim(Beacon.Core.Mcp.McpCallerClaimTypes.Scope, "Execute"));
        await using var app = await NotificationTestKit.StartApiAsync(mediator.Object, executeKey, x => x.MapSubscriptionsEndpoints());

        var attach = await app.GetTestClient().PostAsJsonAsync("/beacon/api/subscriptions/5/recipients", new { recipientIds = new[] { 9 } });

        attach.StatusCode.Should().Be(HttpStatusCode.NoContent);
        mediator.Verify(x => x.Send(It.Is<AddSubscriptionRecipientsCommand>(y => y.SubscriptionId == 5), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task EveryMutatingNotificationRoute_RequiresExecuteOrAdmin()
    {
        await using var app = await NotificationTestKit.StartApiAsync(
            Mock.Of<IMediator>(),
            NotificationTestKit.Admin(),
            x => x.MapSubscriptionsEndpoints().MapRecipientsEndpoints().MapDataQualityEndpoints());

        var mutating = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(x => x.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Any(y => y != HttpMethods.Get) == true)
            .ToList();

        mutating.Should().HaveCountGreaterThan(10);
        foreach (var endpoint in mutating)
        {
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(x => x.Policy).ToList();
            policies.Should().Contain(
                x => x == BeaconApiEndpoints.ExecuteScopePolicyName || x == BeaconApiEndpoints.AdminPolicyName,
                $"'{endpoint.RoutePattern.RawText}' changes where or whether data is delivered");
        }
    }

    [Test]
    public async Task RecipientWrites_ByAnEditor_AreForbidden()
    {
        var mediator = new Mock<IMediator>();
        await using var app = await NotificationTestKit.StartApiAsync(mediator.Object, NotificationTestKit.Interactive(RoleService.RoleNames.Editor), x => x.MapRecipientsEndpoints());
        var client = app.GetTestClient();

        var create = await client.PostAsJsonAsync("/beacon/api/recipients", RecipientBody());
        var update = await client.PutAsJsonAsync("/beacon/api/recipients/3", RecipientBody());
        var delete = await client.DeleteAsync("/beacon/api/recipients/3");
        var list = await client.GetAsync("/beacon/api/recipients");

        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        update.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        delete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        mediator.Verify(x => x.Send(It.IsAny<CreateRecipientCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        mediator.Verify(x => x.Send(It.IsAny<UpdateRecipientCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        mediator.Verify(x => x.Send(It.IsAny<DeleteRecipientCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task RecipientWrites_ByAnAdmin_ReachTheHandlers()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<CreateRecipientCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateRecipientResult(3));
        await using var app = await NotificationTestKit.StartApiAsync(mediator.Object, NotificationTestKit.Admin(), x => x.MapRecipientsEndpoints());
        var client = app.GetTestClient();

        var create = await client.PostAsJsonAsync("/beacon/api/recipients", RecipientBody());
        var update = await client.PutAsJsonAsync("/beacon/api/recipients/3", RecipientBody());
        var delete = await client.DeleteAsync("/beacon/api/recipients/3");

        create.StatusCode.Should().Be(HttpStatusCode.OK);
        update.StatusCode.Should().Be(HttpStatusCode.NoContent);
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        mediator.Verify(x => x.Send(It.IsAny<CreateRecipientCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        mediator.Verify(x => x.Send(It.Is<UpdateRecipientCommand>(y => y.Id == 3), It.IsAny<CancellationToken>()), Times.Once);
        mediator.Verify(x => x.Send(It.Is<DeleteRecipientCommand>(y => y.Id == 3), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static object RecipientBody()
    {
        return new
        {
            name = "hook",
            destination = "https://hooks.example.com/in",
            notificationType = (int)NotificationType.Webhook,
        };
    }

    private static List<string?> Policies(IServiceProvider services, string endpointName)
    {
        return services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(x => x.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == endpointName)
            .Single()
            .Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Select(x => x.Policy)
            .ToList();
    }
}
