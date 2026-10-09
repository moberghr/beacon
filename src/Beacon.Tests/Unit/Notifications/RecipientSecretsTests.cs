using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Recipients;
using Beacon.Core.Notifications;
using Beacon.Core.Services;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// Secrets at rest and in edits: values are stored encrypted (legacy plaintext still reads), shown masked, and a masked
/// or empty value on update keeps the stored secret only while the destination stays exactly the same; a stored value
/// that cannot be decrypted must be entered again.
/// </summary>
[TestFixture]
public class RecipientSecretsTests
{
    private const string Slack = "https://hooks.slack.com/services/T0/B0/SECRET";
    private const string Hook = "https://in.hooks.example.com/in?token=t";
    private const string Jira = "https://acme.atlassian.net;SEC;svc@acme.eu;ATATT-token";
    private const string Headers = "{\"Authorization\":\"Bearer s3cr3t\",\"Api-Key\":\"k1\"}";
    private const string MaskedHeaders = "{\"Authorization\":\"********\",\"Api-Key\":\"********\"}";

    [Test]
    public void Protector_RoundTripsAndReadsLegacyPlaintext()
    {
        var protector = NotificationTestKit.Protector();

        var stored = protector.Protect(Slack);

        stored.Should().StartWith(RecipientSecretProtector.EncryptedPrefix).And.NotContain("SECRET");
        protector.Protect(Slack).Should().NotBe(stored, "every value gets its own nonce");
        protector.Unprotect(stored).Should().Be(Slack);
        protector.Unprotect(Slack).Should().Be(Slack, "a value stored before encryption is read as plaintext");
        protector.ProtectOptional(null).Should().BeNull();
        protector.ProtectOptional(string.Empty).Should().BeNull();
    }

    [Test]
    public void Protector_TryUnprotect_ReturnsNullForAValueItCannotDecrypt()
    {
        var other = new RecipientSecretProtector(new EncryptionService("another-key"));

        NotificationTestKit.Protector().TryUnprotect(other.Protect(Slack)).Should().BeNull();
        NotificationTestKit.Protector().TryUnprotect("enc:not-base64!").Should().BeNull();
    }

    [TestCase(NotificationType.Slack, Slack, "https://hooks.slack.com/********")]
    [TestCase(NotificationType.Teams, "https://x.webhook.office.com:8443/webhookb2/abc", "https://x.webhook.office.com:8443/********")]
    [TestCase(NotificationType.Webhook, "https://user:pw@in.hooks.example.com/in?token=t", "https://***.example.com/********")]
    [TestCase(NotificationType.Webhook, "https://a.b.c.example.com:8443/in", "https://***.example.com:8443/********")]
    [TestCase(NotificationType.Webhook, "https://example.com/in", "https://example.com/********")]
    [TestCase(NotificationType.Webhook, "https://93.184.216.34/in", "https://***/********")]
    [TestCase(NotificationType.Webhook, "https://[2606:2800:220:1::1]/in", "https://***/********")]
    [TestCase(NotificationType.Webhook, "not a url", "********")]
    [TestCase(NotificationType.Jira, Jira, "https://acme.atlassian.net;SEC;svc@acme.eu;********")]
    [TestCase(NotificationType.Jira, "garbage", "********")]
    [TestCase(NotificationType.Email, "ops@example.com", "ops@example.com")]
    public void MaskDestination_KeepsOnlyTheNonSecretParts(NotificationType type, string destination, string expected)
    {
        RecipientSecrets.MaskDestination(type, destination).Should().Be(expected);
    }

    [Test]
    public void MaskHeaders_KeepsNamesOnly()
    {
        RecipientSecrets.MaskHeaders(Headers).Should().Be(MaskedHeaders);
        RecipientSecrets.MaskHeaders(null).Should().BeNull();
        RecipientSecrets.MaskHeaders("{}").Should().BeNull();
        RecipientSecrets.MaskHeaders("not json").Should().BeNull();
    }

    [TestCase("https://in.hooks.example.com/in?token=t", "https://in.hooks.example.com/in?token=t", true)]
    [TestCase("https://in.hooks.example.com/in?token=t", "https://IN.hooks.example.com:443/in?token=t", true)]
    [TestCase("https://in.hooks.example.com/in?token=t", "https://in.hooks.example.com/in?token=u", false)]
    [TestCase("https://in.hooks.example.com/in?token=t", "https://in.hooks.example.com/other?token=t", false)]
    [TestCase("https://in.hooks.example.com/in?token=t", "https://in.hooks.example.com:8443/in?token=t", false)]
    [TestCase("https://in.hooks.example.com/in?token=t", "http://in.hooks.example.com/in?token=t", false)]
    [TestCase("https://in.hooks.example.com/in?token=t", "https://other.example.com/in?token=t", false)]
    [TestCase("https://in.hooks.example.com/in?token=t", null, false)]
    public void SameDestination_ComparesOriginPathAndQuery(string first, string? second, bool expected)
    {
        RecipientSecrets.SameDestination(first, second).Should().Be(expected);
    }

    // --- the editor: create ----------------------------------------------------------------------------------------

    [Test]
    public void Create_WithMaskedValues_IsRefused()
    {
        var editor = NotificationTestKit.Editor();

        var destination = () => editor.Prepare(NotificationType.Webhook, "https://***.example.com/********", null, stored: null);
        var headers = () => editor.Prepare(NotificationType.Webhook, Hook, MaskedHeaders, stored: null);

        destination.Should().Throw<InvalidOperationException>();
        headers.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Create_StoresEncryptedPolicyNormalisedValues()
    {
        var (destination, headers) = NotificationTestKit.Editor().Prepare(NotificationType.Email, " ops@example.com; risk@example.org ", null, stored: null);

        NotificationTestKit.Protector().Unprotect(destination).Should().Be("ops@example.com,risk@example.org");
        headers.Should().BeNull("only webhooks send headers");
    }

    // --- the editor: update keep rules --------------------------------------------------------------------------------

    [TestCase(null)]
    [TestCase("")]
    [TestCase("https://***.example.com/********")]
    public void Update_SameDestination_KeepsTheStoredSecrets(string? submitted)
    {
        var (destination, headers) = Update(Stored(NotificationType.Webhook, Hook, Headers), NotificationType.Webhook, submitted, MaskedHeaders);

        destination.Should().Be(Hook);
        headers.Should().Be(Headers);
    }

    [Test]
    public void Update_SameDestinationWithEmptyHeaders_KeepsThem()
    {
        var (_, headers) = Update(Stored(NotificationType.Webhook, Hook, Headers), NotificationType.Webhook, Hook, null);

        headers.Should().Be(Headers);
    }

    [TestCase("https://in.hooks.example.com:8443/in?token=t")]
    [TestCase("https://in.hooks.example.com/other?token=t")]
    [TestCase("https://in.hooks.example.com/in?token=u")]
    [TestCase("https://collector.example.net/in")]
    public void Update_ChangedDestinationWithMaskedOrEmptyHeaders_IsRefused(string submitted)
    {
        var stored = Stored(NotificationType.Webhook, Hook, Headers);

        var masked = () => Update(stored, NotificationType.Webhook, submitted, MaskedHeaders);
        var empty = () => Update(stored, NotificationType.Webhook, submitted, null);

        masked.Should().Throw<InvalidOperationException>().WithMessage("*same destination*");
        empty.Should().Throw<InvalidOperationException>().WithMessage("*headers again*");
    }

    [Test]
    public void Update_ChangedDestinationWithEmptyObject_RemovesTheHeaders()
    {
        var (destination, headers) = Update(Stored(NotificationType.Webhook, Hook, Headers), NotificationType.Webhook, "https://collector.example.net/in", "{}");

        destination.Should().Be("https://collector.example.net/in");
        headers.Should().BeNull();
    }

    [Test]
    public void Update_ChangedDestinationWithNewHeaderValues_StoresThem()
    {
        var (_, headers) = Update(Stored(NotificationType.Webhook, Hook, Headers), NotificationType.Webhook, "https://collector.example.net/in", "{\"Authorization\":\"Bearer new\"}");

        headers.Should().Be("{\"Authorization\":\"Bearer new\"}");
    }

    [TestCase("{\"Authorization\":\"Bearer ********\"}")]
    [TestCase("{\"Authorization\":\"********x\"}")]
    public void Update_HeaderValueContainingTheMask_IsRefused(string submitted)
    {
        var act = () => Update(Stored(NotificationType.Webhook, Hook, Headers), NotificationType.Webhook, Hook, submitted);

        act.Should().Throw<InvalidOperationException>().WithMessage("*full header value*");
    }

    [Test]
    public void Update_MaskedHeaderWithoutAStoredValue_IsRefused()
    {
        var act = () => Update(Stored(NotificationType.Webhook, Hook, Headers), NotificationType.Webhook, Hook, "{\"X-Api-Key\":\"********\"}");

        act.Should().Throw<InvalidOperationException>();
    }

    [TestCase("https://***.example.com/********x")]
    [TestCase("https://unlisted.example/********")]
    public void Update_ChangedDestinationStillMasked_IsRefused(string submitted)
    {
        var act = () => Update(Stored(NotificationType.Webhook, Hook, null), NotificationType.Webhook, submitted, null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*full destination*");
    }

    [Test]
    public void Update_TeamsMaskedEcho_KeepsTheStoredUrl()
    {
        const string teams = "https://contoso.webhook.office.com/webhookb2/abc/IncomingWebhook/def";

        var (destination, _) = Update(Stored(NotificationType.Teams, teams, null), NotificationType.Teams, "https://contoso.webhook.office.com/********", null);

        destination.Should().Be(teams);
    }

    [Test]
    public void Update_TypeChanged_NeedsTheFullDestinationAndDropsHeaders()
    {
        var stored = Stored(NotificationType.Webhook, Hook, Headers);

        var masked = () => Update(stored, NotificationType.Slack, "https://***.example.com/********", null);
        var (_, headers) = Update(stored, NotificationType.Slack, Slack, MaskedHeaders);

        masked.Should().Throw<InvalidOperationException>();
        headers.Should().BeNull("only webhooks send custom headers");
    }

    [TestCase("https://acme.atlassian.net;OPS;svc@acme.eu;********", "https://acme.atlassian.net;OPS;svc@acme.eu;ATATT-token")]
    [TestCase("https://acme.atlassian.net;SEC;other@acme.eu;********", "https://acme.atlassian.net;SEC;other@acme.eu;ATATT-token")]
    [TestCase("https://ACME.atlassian.net/;SEC;svc@acme.eu;********", "https://ACME.atlassian.net/;SEC;svc@acme.eu;ATATT-token")]
    [TestCase("acme;SEC;svc@acme.eu;********", "acme;SEC;svc@acme.eu;ATATT-token")]
    public void Update_JiraWithMaskedToken_KeepsTheTokenForTheSameSite(string submitted, string expected)
    {
        var (destination, _) = Update(Stored(NotificationType.Jira, Jira, null), NotificationType.Jira, submitted, null);

        destination.Should().Be(expected);
    }

    [TestCase("https://other.atlassian.net;SEC;svc@acme.eu;********")]
    [TestCase("other;SEC;svc@acme.eu;********")]
    public void Update_JiraWithMaskedTokenOnANewSite_IsRefused(string submitted)
    {
        var act = () => Update(Stored(NotificationType.Jira, Jira, null), NotificationType.Jira, submitted, null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*token again*");
    }

    [Test]
    public void Update_LegacyPlaintextRowToANewHost_NeedsHeadersAgainAndEncryptsWhatItStores()
    {
        var stored = new StoredRecipientSecrets(NotificationType.Webhook, Hook, Headers);

        var masked = () => Update(stored, NotificationType.Webhook, "https://collector.example.net/in", MaskedHeaders);
        var (destination, headers) = NotificationTestKit.Editor().Prepare(NotificationType.Webhook, "https://collector.example.net/in", "{}", stored);

        masked.Should().Throw<InvalidOperationException>();
        destination.Should().StartWith(RecipientSecretProtector.EncryptedPrefix);
        headers.Should().BeNull();
    }

    [Test]
    public void Update_UnreadableStoredDestination_MustBeEnteredAgain()
    {
        var foreign = new RecipientSecretProtector(new EncryptionService("another-key"));
        var stored = new StoredRecipientSecrets(NotificationType.Webhook, foreign.Protect(Hook), null);

        var empty = () => NotificationTestKit.Editor().Prepare(NotificationType.Webhook, null, null, stored);
        var masked = () => NotificationTestKit.Editor().Prepare(NotificationType.Webhook, "https://***.example.com/********", null, stored);
        var (destination, _) = NotificationTestKit.Editor().Prepare(NotificationType.Webhook, Hook, null, stored);

        empty.Should().Throw<InvalidOperationException>().WithMessage("*could not be read*");
        masked.Should().Throw<InvalidOperationException>().WithMessage("*could not be read*");
        NotificationTestKit.Protector().Unprotect(destination).Should().Be(Hook);
    }

    [Test]
    public void Update_UnreadableStoredHeaders_MustBeEnteredAgainOrRemoved()
    {
        var foreign = new RecipientSecretProtector(new EncryptionService("another-key"));
        var stored = new StoredRecipientSecrets(NotificationType.Webhook, NotificationTestKit.Protector().Protect(Hook), foreign.Protect(Headers));

        var empty = () => NotificationTestKit.Editor().Prepare(NotificationType.Webhook, Hook, null, stored);
        var masked = () => NotificationTestKit.Editor().Prepare(NotificationType.Webhook, Hook, MaskedHeaders, stored);
        var (_, removed) = NotificationTestKit.Editor().Prepare(NotificationType.Webhook, Hook, "{}", stored);

        empty.Should().Throw<InvalidOperationException>().WithMessage("*could not be read*");
        masked.Should().Throw<InvalidOperationException>().WithMessage("*could not be read*");
        removed.Should().BeNull();
    }

    [Test]
    public void ReadMasked_FlagsUnreadableValues()
    {
        var foreign = new RecipientSecretProtector(new EncryptionService("another-key"));
        var editor = NotificationTestKit.Editor();

        var readable = editor.ReadMasked(NotificationType.Webhook, NotificationTestKit.Protector().Protect(Hook), NotificationTestKit.Protector().Protect(Headers));
        var badDestination = editor.ReadMasked(NotificationType.Webhook, foreign.Protect(Hook), null);
        var badHeaders = editor.ReadMasked(NotificationType.Webhook, NotificationTestKit.Protector().Protect(Hook), foreign.Protect(Headers));

        readable.Should().Be(new MaskedRecipientSecrets("https://***.example.com/********", MaskedHeaders, false));
        badDestination.Unreadable.Should().BeTrue();
        badDestination.Destination.Should().Be(RecipientSecrets.Mask);
        badHeaders.Unreadable.Should().BeTrue();
        badHeaders.HeadersJson.Should().BeNull();
    }

    // --- the handlers -----------------------------------------------------------------------------------------------

    [Test]
    public async Task UpdateHandler_WithTheMaskedValuesEchoedBack_KeepsTheStoredSecrets()
    {
        var store = StoreWith(NotificationType.Webhook, Hook, Headers);

        await UpdateHandler(store).Handle(
            new UpdateRecipientCommand(1, "renamed", "new description", "https://***.example.com/********", (int)NotificationType.Webhook, MaskedHeaders, null),
            CancellationToken.None);

        var recipient = store.Recipients.Single();
        recipient.Name.Should().Be("renamed");
        NotificationTestKit.Protector().Unprotect(recipient.Destination).Should().Be(Hook);
        NotificationTestKit.Protector().Unprotect(recipient.HeadersJson!).Should().Be(Headers);
        store.Saves.Should().Be(1);
    }

    [Test]
    public async Task UpdateHandler_OfALegacyPlaintextRow_EncryptsIt()
    {
        var store = new RecipientStore();
        store.Recipients.Add(new Recipient { Id = 1, Name = "slack", NotificationType = NotificationType.Slack, Destination = Slack });

        await UpdateHandler(store).Handle(
            new UpdateRecipientCommand(1, "slack", null, "https://hooks.slack.com/********", (int)NotificationType.Slack, null, null),
            CancellationToken.None);

        var recipient = store.Recipients.Single();
        recipient.Destination.Should().StartWith(RecipientSecretProtector.EncryptedPrefix).And.NotContain("SECRET");
        NotificationTestKit.Protector().Unprotect(recipient.Destination).Should().Be(Slack);
    }

    [Test]
    public async Task UpdateHandler_RefusedChange_WritesNothing()
    {
        var store = StoreWith(NotificationType.Webhook, Hook, Headers);
        var before = (store.Recipients.Single().Destination, store.Recipients.Single().HeadersJson);

        var act = () => UpdateHandler(store).Handle(
            new UpdateRecipientCommand(1, "hook", null, "https://collector.example.net/in", (int)NotificationType.Webhook, null, null),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        store.Saves.Should().Be(0);
        (store.Recipients.Single().Destination, store.Recipients.Single().HeadersJson).Should().Be(before);
    }

    [Test]
    public async Task UpdateHandler_ToADestinationThePolicyRefuses_WritesNothing()
    {
        var store = StoreWith(NotificationType.Webhook, Hook, null);

        var act = () => UpdateHandler(store).Handle(
            new UpdateRecipientCommand(1, "hook", null, "https://169.254.169.254/latest/meta-data", (int)NotificationType.Webhook, null, null),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a public address*");
        store.Saves.Should().Be(0);
    }

    [Test]
    public async Task CreateHandler_ADestinationOrHeadersThePolicyRefuses_WritesNothing()
    {
        var store = new RecipientStore();
        var handler = new CreateRecipientHandler(NotificationTestKit.Factory(store), NotificationTestKit.Editor());

        var destination = () => handler.Handle(
            new CreateRecipientCommand("hook", null, "http://10.0.0.5:8080/admin", (int)NotificationType.Webhook, null, null),
            CancellationToken.None);
        var headers = () => handler.Handle(
            new CreateRecipientCommand("hook", null, "https://hooks.example.com/in", (int)NotificationType.Webhook, "{\"X-Forwarded-For\":\"127.0.0.1\"}", null),
            CancellationToken.None);

        await destination.Should().ThrowAsync<InvalidOperationException>();
        await headers.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not allowed*");
        store.Added.Should().BeEmpty();
        store.Saves.Should().Be(0);
    }

    private static (string Destination, string? HeadersJson) Update(StoredRecipientSecrets stored, NotificationType type, string? destination, string? headersJson)
    {
        var (protectedDestination, protectedHeaders) = NotificationTestKit.Editor().Prepare(type, destination, headersJson, stored);
        var protector = NotificationTestKit.Protector();

        return (protector.Unprotect(protectedDestination), protector.UnprotectOptional(protectedHeaders));
    }

    private static StoredRecipientSecrets Stored(NotificationType type, string destination, string? headers)
    {
        var protector = NotificationTestKit.Protector();

        return new StoredRecipientSecrets(type, protector.Protect(destination), protector.ProtectOptional(headers));
    }

    private static UpdateRecipientHandler UpdateHandler(RecipientStore store)
    {
        return new UpdateRecipientHandler(NotificationTestKit.Factory(store), NotificationTestKit.Editor());
    }

    private static RecipientStore StoreWith(NotificationType type, string destination, string? headers)
    {
        var protector = NotificationTestKit.Protector();
        var store = new RecipientStore();
        store.Recipients.Add(new Recipient
        {
            Id = 1,
            Name = "hook",
            NotificationType = type,
            Destination = protector.Protect(destination),
            HeadersJson = protector.ProtectOptional(headers),
        });

        return store;
    }
}
