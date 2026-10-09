using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
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
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Recipients;
using Beacon.Core.Notifications;
using Beacon.Core.Services;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// Recipient secrets (the Jira <c>site;project;email;apiToken</c> tuple, Slack and Teams webhook URLs, webhook auth
/// headers) never leave the server in a read: non-admins get names and types only, admins get masked values, and the
/// list's search does not read destinations. Covers rows encrypted at rest and rows stored before encryption.
/// </summary>
[TestFixture]
public class RecipientSecretExposureTests
{
    private const string JiraToken = "ATATT-secret";
    private const string JiraDestination = "bank;SEC;svc@bank.eu;" + JiraToken;
    private const string WebhookBearer = "Bearer s3cr3t";
    private const string WebhookHeaders = "{\"Authorization\":\"" + WebhookBearer + "\"}";
    private const string SlackWebhook = "https://hooks.slack.com/services/T000/B000/XXXXSECRETXXXX";
    private const string LegacySlackWebhook = "https://hooks.slack.com/services/T111/B111/LEGACYSECRET";

    private static readonly string[] Secrets = [JiraToken, WebhookBearer, "XXXXSECRETXXXX", "LEGACYSECRET", "int.bank/hook"];

    [Test]
    public async Task GetRecipients_ForANonAdmin_ReturnsNamesAndTypesOnly()
    {
        var handler = Handler(NotificationTestKit.Interactive(RoleService.RoleNames.Editor));

        var result = await handler.Handle(new GetRecipientsQuery { PageSize = 200 }, CancellationToken.None);

        result.Items.Select(x => x.Name).Should().BeEquivalentTo("jira", "webhook", "slack", "legacy-slack");
        result.Items.Should().OnlyContain(x => x.Destination == null && x.HeadersJson == null && x.BodyTemplate == null && !x.SecretsUnreadable);
        JsonSerializer.Serialize(result).Should().NotContainAny(Secrets);
    }

    [Test]
    public async Task GetRecipients_ForAnAdmin_ReturnsMaskedValuesOnly()
    {
        var handler = Handler(NotificationTestKit.Admin());

        var result = await handler.Handle(new GetRecipientsQuery { PageSize = 200 }, CancellationToken.None);

        var byName = result.Items.ToDictionary(x => x.Name);
        byName["jira"].Destination.Should().Be("bank;SEC;svc@bank.eu;********");
        byName["slack"].Destination.Should().Be("https://hooks.slack.com/********");
        byName["legacy-slack"].Destination.Should().Be("https://hooks.slack.com/********", "a row stored before encryption is masked too");
        byName["webhook"].Destination.Should().Be("https://***.example.com/********", "only the last two labels of a webhook host are shown");
        byName["webhook"].HeadersJson.Should().Be("{\"Authorization\":\"********\"}");
        JsonSerializer.Serialize(result).Should().NotContainAny(Secrets);
    }

    [Test]
    public async Task GetRecipients_SearchDoesNotMatchOnDestinations()
    {
        var handler = Handler(NotificationTestKit.Admin());

        var result = await handler.Handle(new GetRecipientsQuery { PageSize = 200, Search = "LEGACYSECRET" }, CancellationToken.None);

        result.Items.Should().BeEmpty("the search covers names and descriptions, never the stored destination");
    }

    [TestCase("-storedDestination")]
    [TestCase("-storedHeadersJson")]
    [TestCase("-storedBodyTemplate")]
    [TestCase("-bodyTemplate")]
    public async Task GetRecipients_SortingByAStoredColumn_IsIgnored(string sort)
    {
        var handler = Handler(NotificationTestKit.Interactive(RoleService.RoleNames.Editor));

        var result = await handler.Handle(new GetRecipientsQuery { PageSize = 200, Sort = sort }, CancellationToken.None);

        result.Items.Select(x => x.Name).Should().Equal(
            ["jira", "legacy-slack", "slack", "webhook"],
            "an unknown sort column falls back to the name order, so the stored values' order is not observable");
    }

    [Test]
    public async Task GetRecipients_ForAnAdmin_FlagsAValueThatCannotBeDecrypted()
    {
        var foreign = new RecipientSecretProtector(new EncryptionService("another-key"));
        var store = new RecipientStore();
        store.Recipients.Add(new Recipient { Id = 7, Name = "rotated", NotificationType = NotificationType.Slack, Destination = foreign.Protect(SlackWebhook) });
        var handler = new GetRecipientsHandler(NotificationTestKit.Factory(store), NotificationTestKit.UserContext(NotificationTestKit.Admin()), NotificationTestKit.Editor());

        var result = await handler.Handle(new GetRecipientsQuery { PageSize = 200 }, CancellationToken.None);

        var entry = result.Items.Should().ContainSingle().Subject;
        entry.SecretsUnreadable.Should().BeTrue();
        entry.Destination.Should().Be(RecipientSecrets.Mask);
    }

    [Test]
    public async Task RecipientRoutes_ForAReadScopedApiKey_DiscloseNoSecretAndRefuseWrites()
    {
        // Mirrors MapBeaconApi's group wiring: MapGroup("/beacon/api").RequireAuthorization(AuthPolicyName), followed
        // by MapRecipientsEndpoints(). The mediator runs the real GetRecipientsHandler for the caller.
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<GetRecipientsQuery>(), It.IsAny<CancellationToken>()))
            .Returns((GetRecipientsQuery query, CancellationToken ct) =>
                Handler(NotificationTestKit.ReadScopedApiKey()).Handle(query, ct));

        await using var app = await NotificationTestKit.StartApiAsync(mediator.Object, NotificationTestKit.ReadScopedApiKey(), x => x.MapRecipientsEndpoints());

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        foreach (var name in new[] { "CreateRecipient", "UpdateRecipient", "DeleteRecipient" })
        {
            Policies(endpoints, name).Should().Contain(BeaconApiEndpoints.AdminPolicyName, $"{name} decides where Beacon sends data");
        }

        var client = app.GetTestClient();
        var list = await client.GetAsync("/beacon/api/recipients?pageSize=200");
        var body = await list.Content.ReadAsStringAsync();

        list.StatusCode.Should().Be(HttpStatusCode.OK, "any reader may list recipients to attach them");
        body.Should().Contain("legacy-slack");
        body.Should().NotContainAny(Secrets);

        var create = await client.PostAsJsonAsync("/beacon/api/recipients", new
        {
            name = "copy",
            destination = "https://hooks.slack.com/services/T/B/C",
            notificationType = (int)NotificationType.Slack,
        });
        var update = await client.PutAsJsonAsync("/beacon/api/recipients/3", new
        {
            name = "slack",
            destination = "https://hooks.slack.com/services/T/B/C",
            notificationType = (int)NotificationType.Slack,
        });
        var delete = await client.DeleteAsync("/beacon/api/recipients/3");

        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        update.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        delete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        mediator.Verify(x => x.Send(It.IsAny<CreateRecipientCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        mediator.Verify(x => x.Send(It.IsAny<UpdateRecipientCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        mediator.Verify(x => x.Send(It.IsAny<DeleteRecipientCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task CreateRecipient_StoresDestinationAndHeadersEncrypted()
    {
        var store = new RecipientStore();
        var protector = NotificationTestKit.Protector();
        var handler = new CreateRecipientHandler(NotificationTestKit.Factory(store), NotificationTestKit.Editor());

        await handler.Handle(
            new CreateRecipientCommand("hook", null, "https://hooks.example.com/in/" + JiraToken, (int)NotificationType.Webhook, WebhookHeaders, null),
            CancellationToken.None);

        var saved = store.Added.Should().ContainSingle().Subject;
        saved.Destination.Should().StartWith(RecipientSecretProtector.EncryptedPrefix).And.NotContain(JiraToken);
        saved.HeadersJson.Should().StartWith(RecipientSecretProtector.EncryptedPrefix).And.NotContain(WebhookBearer);
        protector.Unprotect(saved.Destination).Should().Be("https://hooks.example.com/in/" + JiraToken);
        protector.Unprotect(saved.HeadersJson!).Should().Be(WebhookHeaders);
        store.Saves.Should().Be(1);
    }

    private static GetRecipientsHandler Handler(ClaimsPrincipal user)
    {
        return new GetRecipientsHandler(
            NotificationTestKit.Factory(Seed()),
            NotificationTestKit.UserContext(user),
            NotificationTestKit.Editor());
    }

    private static RecipientStore Seed()
    {
        var protector = NotificationTestKit.Protector();
        var store = new RecipientStore();
        store.Recipients.AddRange(
        [
            new Recipient { Id = 1, Name = "jira", NotificationType = NotificationType.Jira, Destination = protector.Protect(JiraDestination) },
            new Recipient
            {
                Id = 2,
                Name = "webhook",
                NotificationType = NotificationType.Webhook,
                Destination = protector.Protect("https://int.example.com/hook?token=int.bank/hook"),
                HeadersJson = protector.Protect(WebhookHeaders),
                BodyTemplate = "{\"text\":\"{{SubscriptionName}}\"}",
            },
            new Recipient { Id = 3, Name = "slack", NotificationType = NotificationType.Slack, Destination = protector.Protect(SlackWebhook) },

            // Stored before encryption at rest: plaintext until it is next saved or the re-encryption job runs.
            new Recipient { Id = 4, Name = "legacy-slack", NotificationType = NotificationType.Slack, Destination = LegacySlackWebhook },
        ]);

        return store;
    }

    private static List<string?> Policies(IEnumerable<Endpoint> endpoints, string endpointName)
    {
        return endpoints
            .Where(x => x.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == endpointName)
            .Single()
            .Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Select(x => x.Policy)
            .ToList();
    }
}
