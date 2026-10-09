using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Adapters;
using Beacon.Core.Adapters.Jira;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using Beacon.Core.PostgreSql;
using Beacon.Core.Worker;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// The library wiring: <c>Beacon:Notifications</c> binds and is validated, adapters and the Jira client factory resolve
/// on the hardened named client, and the re-encryption service is available to the host's job.
/// </summary>
[TestFixture]
public class NotificationRegistrationTests
{
    [Test]
    public void Configuration_BindsTheNotificationSection()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Beacon:Notifications:AllowedHosts:Jira:0"] = "jira.bank.example",
            ["Beacon:Notifications:AllowedHosts:Email:0"] = "bank.example",
            ["Beacon:Notifications:DisabledTypes:0"] = "Webhook",
            ["Beacon:Notifications:AllowedPrivateNetworks:Jira:0"] = "10.20.0.0/16",
            ["Beacon:Notifications:AllowedHeaders:0"] = "X-Team",
            ["Beacon:Notifications:Nat64Prefixes:0"] = "2001:db8:64::/96",
            ["Beacon:Notifications:UseSystemProxy"] = "true",
            ["Beacon:Notifications:RequireEncryptedSecrets"] = "true",
        });

        var options = provider.GetRequiredService<IOptions<NotificationChannelOptions>>().Value;

        options.AllowedHosts.Jira.Should().Equal("jira.bank.example");
        options.AllowedHosts.Email.Should().Equal("bank.example");
        options.DisabledTypes.Should().Equal(NotificationType.Webhook);
        options.AllowedPrivateNetworks.Jira.Should().Equal("10.20.0.0/16");
        options.AllowedPrivateNetworks.Webhook.Should().BeEmpty();
        options.AllowedHeaders.Should().Equal("X-Team");
        options.Nat64Prefixes.Should().Equal("2001:db8:64::/96");
        options.UseSystemProxy.Should().BeTrue();
        options.RequireEncryptedSecrets.Should().BeTrue();
        provider.GetRequiredService<OutboundAddressPolicy>().UsesSystemProxy.Should().BeTrue();
    }

    [Test]
    public void Configuration_ByDefault_ConnectsDirectly()
    {
        using var provider = BuildProvider([]);

        var options = provider.GetRequiredService<IOptions<NotificationChannelOptions>>().Value;
        options.UseSystemProxy.Should().BeFalse();
        options.RequireEncryptedSecrets.Should().BeFalse();
        provider.GetRequiredService<OutboundAddressPolicy>().UsesSystemProxy.Should().BeFalse();
    }

    [TestCase("UseSystemProxy")]
    [TestCase("RequireEncryptedSecrets")]
    public void Configuration_WithANonBooleanSwitch_FailsAtStartup(string key)
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"Beacon:Notifications:{key}"] = "sometimes",
        });

        var act = () => provider.GetRequiredService<IOptions<NotificationChannelOptions>>().Value;

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{key}*");
    }

    [Test]
    public void Configuration_ProxyModeWithoutWebhookHosts_FailsValidation()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Beacon:Notifications:UseSystemProxy"] = "true",
        });

        var act = () => provider.GetRequiredService<IOptions<NotificationChannelOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*AllowedHosts:Webhook*");
    }

    [Test]
    public void Configuration_WithAMalformedEntry_FailsValidation()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Beacon:Notifications:AllowedPrivateNetworks:Teams:0"] = "10.1.2.3/8",
        });

        var act = () => provider.GetRequiredService<IOptions<NotificationChannelOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*AllowedPrivateNetworks:Teams*'10.1.2.3/8'*");
    }

    [Test]
    public void AdaptersAndServices_ResolveOnTheNotificationClient()
    {
        using var provider = BuildProvider([]);

        provider.GetServices<IAdapter>().Select(x => x.NotificationType)
            .Should().BeEquivalentTo([NotificationType.Teams, NotificationType.Slack, NotificationType.Jira, NotificationType.Webhook]);
        provider.GetRequiredService<IJiraRestClientFactory>().Should().NotBeNull();
        provider.GetRequiredService<IRecipientSecretEncryptionService>().Should().NotBeNull();

        provider.GetRequiredService<RecipientSecretEditor>().Should().NotBeNull();
        provider.GetServices<IValidateOptions<NotificationChannelOptions>>().Should().ContainSingle();

        foreach (var type in OutboundAddressPolicy.HttpTypes)
        {
            var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(NotificationHttpClient.NameFor(type));
            var chain = new List<HttpMessageHandler>();
            for (HttpMessageHandler? current = handler; current != null; current = (current as DelegatingHandler)?.InnerHandler)
            {
                chain.Add(current);
            }

            chain.OfType<SocketsHttpHandler>().Should().ContainSingle().Which.AllowAutoRedirect.Should().BeFalse();
        }
    }

    [Test]
    public async Task EncryptRecipientSecretsJob_RunsTheServiceWithTheJobsToken()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new Mock<IRecipientSecretEncryptionService>();
        service
            .Setup(x => x.EncryptStoredSecretsAsync(cancellation.Token))
            .ReturnsAsync(new RecipientSecretEncryptionResult(3, 2, 1, 0));
        var logger = new CapturingLogger<Beacon.SampleProject.Warp.Jobs.EncryptRecipientSecretsJobHandler>();
        var handler = new Beacon.SampleProject.Warp.Jobs.EncryptRecipientSecretsJobHandler(service.Object, logger);

        await handler.HandleAsync(new Beacon.SampleProject.Warp.Jobs.EncryptRecipientSecretsJob(), cancellation.Token);

        service.Verify(x => x.EncryptStoredSecretsAsync(cancellation.Token), Times.Once);
        logger.Entries.Should().ContainSingle().Which.Message.Should().Contain("2 of 3");
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        settings["Beacon:EncryptionKey"] = Convert.ToBase64String(new byte[32]);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>())
            .UsePostgreSql("Host=localhost;Database=unused;Username=unused;Password=unused");

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
