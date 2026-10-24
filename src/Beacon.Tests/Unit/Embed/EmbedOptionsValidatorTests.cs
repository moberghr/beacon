using System.Security.Cryptography;
using System.Text;
using Beacon.Core;
using Beacon.Core.PostgreSql;
using Beacon.Core.Services.Embed;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// <c>Beacon:Embed</c> is checked at startup: off, nothing is required; on, the signing key must meet the encryption key's
/// requirements under its own name and must not be the encryption key, and the limits must be in range. No message ever
/// contains a key.
/// </summary>
[TestFixture]
public class EmbedOptionsValidatorTests
{
    private static readonly EmbedOptionsValidator Validator = new(TestEncryptionKey.Value);

    [Test]
    public void Off_NeedsNoKey()
    {
        Validator.Validate(null, new EmbedOptions()).Succeeded.Should().BeTrue();
    }

    [Test]
    public void On_WithAStrongSeparateKey_Succeeds()
    {
        Validator.Validate(null, Enabled(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))).Succeeded.Should().BeTrue();
    }

    [TestCase(null, "must be configured")]
    [TestCase("", "must be configured")]
    [TestCase("${EMBED_KEY}", "does not expand placeholders")]
    [TestCase("c2hvcnQ=", "at least 32 random bytes")]
    [TestCase("not base64 at all, but long enough to count as thirty-two", "at least 32 random bytes")]
    public void On_RefusesAKeyThatIsMissingPlaceholderOrShort(string? key, string expected)
    {
        var result = Validator.Validate(null, Enabled(key));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Beacon:Embed:SigningKey").And.Contain(expected);
        result.FailureMessage.Should().NotContain("Beacon:EncryptionKey ").And.NotContain("PreviousEncryptionKeys");
        if (!string.IsNullOrEmpty(key))
        {
            result.FailureMessage.Should().NotContain(key);
        }
    }

    [Test]
    public void On_RefusesTextOrALowEntropyKey()
    {
        var text = Convert.ToBase64String(Encoding.UTF8.GetBytes("a passphrase that is long enough for thirty-two"));
        var repeated = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());

        foreach (var key in new[] { text, repeated })
        {
            var result = Validator.Validate(null, Enabled(key));
            result.Failed.Should().BeTrue();
            result.FailureMessage.Should().Contain("Beacon:Embed:SigningKey decodes to bytes that are not random").And.NotContain(key);
        }
    }

    [Test]
    public void On_RefusesTheEncryptionKey()
    {
        var result = Validator.Validate(null, Enabled($" {TestEncryptionKey.Value} "));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("must not be Beacon:EncryptionKey").And.NotContain(TestEncryptionKey.Value);
    }

    [TestCase(nameof(EmbedOptions.MaxTokenTtlSeconds), 901)]
    [TestCase(nameof(EmbedOptions.TokenTtlSeconds), 0)]
    [TestCase(nameof(EmbedOptions.MaxGrantDays), 0)]
    [TestCase(nameof(EmbedOptions.MaxRows), 0)]
    [TestCase(nameof(EmbedOptions.MaxOrigins), 0)]
    public void ALimitOutOfRange_IsRefused_EvenWhileOff(string property, int value)
    {
        var options = new EmbedOptions();
        typeof(EmbedOptions).GetProperty(property)!.SetValue(options, value);

        var result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain($"Beacon:Embed:{property}");
    }

    [Test]
    public void ATokenLifetimeAboveItsMaximum_IsRefused()
    {
        var result = Validator.Validate(null, new EmbedOptions { TokenTtlSeconds = 600, MaxTokenTtlSeconds = 300 });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Beacon:Embed:TokenTtlSeconds must be between 1 and 300");
    }

    [Test]
    public void TheHost_FailsAtStartup_WhenEmbeddingIsOnWithoutAKey()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:EncryptionKey"] = TestEncryptionKey.Value,
                ["Beacon:Embed:Enabled"] = "true"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>())
            .UsePostgreSql("Host=localhost;Database=unused;Username=unused;Password=unused");
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<EmbedOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*Beacon:Embed:SigningKey must be configured*");
    }

    private static EmbedOptions Enabled(string? key) => new() { Enabled = true, SigningKey = key };

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
