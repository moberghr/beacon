using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Recipients;
using Beacon.Core.Notifications;
using Beacon.Core.Services;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// The host-facing <see cref="IRecipientService"/> follows the same rules as the REST handlers: the destination policy,
/// encryption at rest, masked reads (for encrypted and legacy rows alike), search that never reads destinations, and
/// keep-or-refuse on update. A rule it breaks comes back as an unsuccessful response, not an exception.
/// </summary>
[TestFixture]
public class RecipientServiceTests
{
    private const string Hook = "https://in.hooks.example.com/in?token=PATHSECRET";
    private const string Headers = "{\"Authorization\":\"Bearer s3cr3t\"}";

    [Test]
    public async Task Create_RefusedByThePolicy_ReturnsTheReasonAndWritesNothing()
    {
        var store = new RecipientStore();

        var response = await Service(store).CreateRecipient(Data("hook", "http://10.0.0.5:8080/admin", NotificationType.Webhook), CancellationToken.None);

        response.Success.Should().BeFalse();
        response.Message.Should().Contain("https URL");
        store.Added.Should().BeEmpty();
        store.Saves.Should().Be(0);
    }

    [Test]
    public async Task Create_StoresTheSecretsEncrypted()
    {
        var store = new RecipientStore();

        var response = await Service(store).CreateRecipient(Data("hook", Hook, NotificationType.Webhook, Headers), CancellationToken.None);

        response.Success.Should().BeTrue();
        var saved = store.Added.Should().ContainSingle().Subject;
        saved.Destination.Should().StartWith(RecipientSecretProtector.EncryptedPrefix).And.NotContain("PATHSECRET");
        saved.HeadersJson.Should().StartWith(RecipientSecretProtector.EncryptedPrefix).And.NotContain("s3cr3t");
        NotificationTestKit.Protector().Unprotect(saved.Destination).Should().Be(Hook);
    }

    [Test]
    public async Task GetRecipients_ReturnsMaskedValuesForEncryptedAndLegacyRows()
    {
        var store = new RecipientStore();
        var protector = NotificationTestKit.Protector();
        store.Recipients.Add(new Recipient { Id = 1, Name = "encrypted", NotificationType = NotificationType.Webhook, Destination = protector.Protect(Hook), HeadersJson = protector.Protect(Headers) });
        store.Recipients.Add(new Recipient { Id = 2, Name = "legacy", NotificationType = NotificationType.Webhook, Destination = Hook, HeadersJson = Headers });

        var recipients = await Service(store).GetRecipients(null, null, CancellationToken.None);

        recipients.Should().HaveCount(2);
        recipients.Should().OnlyContain(x => x.Destination == "https://***.example.com/********");
        recipients.Should().OnlyContain(x => x.HeadersJson == "{\"Authorization\":\"********\"}");
        JsonSerializer.Serialize(recipients).Should().NotContain("PATHSECRET").And.NotContain("s3cr3t");
    }

    [Test]
    public async Task GetRecipients_SearchDoesNotMatchOnDestinations()
    {
        var store = new RecipientStore();
        store.Recipients.Add(new Recipient { Id = 2, Name = "legacy", NotificationType = NotificationType.Webhook, Destination = Hook });

        var recipients = await Service(store).GetRecipients(null, "PATHSECRET", CancellationToken.None);

        recipients.Should().BeEmpty();
    }

    [Test]
    public async Task Update_WithTheMaskedValues_KeepsTheStoredSecrets()
    {
        var store = StoreWith(Hook, Headers);
        var data = Data("hook", "https://***.example.com/********", NotificationType.Webhook, "{\"Authorization\":\"********\"}", recipientId: 1);

        var response = await Service(store).UpdateRecipient(data, CancellationToken.None);

        response.Success.Should().BeTrue();
        NotificationTestKit.Protector().Unprotect(store.Recipients.Single().Destination).Should().Be(Hook);
        NotificationTestKit.Protector().Unprotect(store.Recipients.Single().HeadersJson!).Should().Be(Headers);
    }

    [Test]
    public async Task Update_ToANewHostWithMaskedHeaders_ReturnsTheReasonAndWritesNothing()
    {
        var store = StoreWith(Hook, Headers);
        var before = store.Recipients.Single().Destination;
        var data = Data("hook", "https://collector.example.net/in", NotificationType.Webhook, "{\"Authorization\":\"********\"}", recipientId: 1);

        var response = await Service(store).UpdateRecipient(data, CancellationToken.None);

        response.Success.Should().BeFalse();
        response.Message.Should().Contain("same destination");
        store.Saves.Should().Be(0);
        store.Recipients.Single().Destination.Should().Be(before);
    }

    private static RecipientService Service(RecipientStore store)
    {
        return new RecipientService(NotificationTestKit.Factory(store), NotificationTestKit.Editor());
    }

    private static RecipientData Data(string name, string destination, NotificationType type, string? headers = null, int? recipientId = null)
    {
        return new RecipientData
        {
            RecipientId = recipientId,
            Name = name,
            Destination = destination,
            NotificationType = type,
            HeadersJson = headers,
        };
    }

    private static RecipientStore StoreWith(string destination, string? headers)
    {
        var protector = NotificationTestKit.Protector();
        var store = new RecipientStore();
        store.Recipients.Add(new Recipient
        {
            Id = 1,
            Name = "hook",
            NotificationType = NotificationType.Webhook,
            Destination = protector.Protect(destination),
            HeadersJson = protector.ProtectOptional(headers),
        });

        return store;
    }
}
