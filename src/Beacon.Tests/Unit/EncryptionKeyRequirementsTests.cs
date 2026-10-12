using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.PostgreSql;
using Beacon.Core.Services;
using Beacon.Core.Worker;

namespace Beacon.Tests.Unit;

/// <summary>
/// <c>Beacon:EncryptionKey</c> is checked at startup in every environment: base64 of at least 32 bytes, never a value the
/// sample host or the documentation printed, never a <c>${...}</c> placeholder; the refusal never shows the value. Values
/// written with a replaced key stay readable through <c>Beacon:PreviousEncryptionKeys</c>.
/// </summary>
[TestFixture]
public class EncryptionKeyRequirementsTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void AMissingKey_IsRefused(string? key)
    {
        EncryptionKeyRequirements.Problem(key).Should().StartWith("Beacon:EncryptionKey must be configured.");
    }

    [TestCase("${BEACON_ENCRYPTION_KEY}")]
    [TestCase("prefix-${SECRET}")]
    public void APlaceholder_IsRefused_BecauseConfigurationDoesNotExpandIt(string key)
    {
        var problem = EncryptionKeyRequirements.Problem(key);

        problem.Should().Contain("does not expand placeholders");
        problem.Should().NotContain(key);
    }

    [Test]
    public void TheSamplePlaceholder_IsRefusedAsPubliclyKnown_InEveryEnvironment()
    {
        var problem = EncryptionKeyRequirements.Problem(ServiceConfiguration.DefaultEncryptionKey);

        problem.Should().Contain("publicly known").And.Contain("Beacon:PreviousEncryptionKeys");
        problem.Should().NotContain(ServiceConfiguration.DefaultEncryptionKey);
    }

    [Test]
    public void AKeyWhoseDigestIsPublished_IsRefused_EvenWhenItIsStrong()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var published = new HashSet<string> { EncryptionKeyRequirements.Digest(key) };

        EncryptionKeyRequirements.Problem(key, published).Should().Contain("publicly known");
        EncryptionKeyRequirements.Problem($"  {key}  ", published).Should().Contain("publicly known", "surrounding whitespace changes nothing");
        EncryptionKeyRequirements.Problem(key).Should().BeNull("the shipped list holds only the published values");
    }

    [Test]
    public void ThePublishedValues_AreKeptAsSha256Digests()
    {
        EncryptionKeyRequirements.PublishedKeyDigests.Should().HaveCount(12)
            .And.OnlyContain(x => x.Length == 64 && x.All(y => char.IsAsciiHexDigitLower(y) || char.IsAsciiDigit(y)));
        EncryptionKeyRequirements.PublishedKeyDigests
            .Should().Contain(EncryptionKeyRequirements.Digest(ServiceConfiguration.DefaultEncryptionKey));
    }

    [TestCase("correct horse battery staple, a long passphrase")]
    [TestCase("not base64 at all!")]
    public void APassphrase_IsRefused(string key)
    {
        var problem = EncryptionKeyRequirements.Problem(key);

        problem.Should().Contain("base64 that decodes to at least 32 random bytes");
        problem.Should().NotContain(key);
    }

    [TestCase(16, false)]
    [TestCase(31, false)]
    [TestCase(32, true)]
    [TestCase(64, true)]
    public void ABase64Key_IsAcceptedFrom32Bytes(int length, bool accepted)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(length));

        var problem = EncryptionKeyRequirements.Problem(key);

        if (accepted)
        {
            problem.Should().BeNull();
            return;
        }

        problem.Should().Contain("at least 32 random bytes").And.NotContain(key);
    }

    // Base64 of text (a passphrase) or of a repeated pattern decodes to 32 bytes, but not to random ones.
    [TestCase("a passphrase someone wrote down!")]
    [TestCase("correct horse battery staple 2026")]
    public void Base64OfText_IsRefused(string text)
    {
        var key = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

        var problem = EncryptionKeyRequirements.Problem(key);

        problem.Should().Contain("not random").And.NotContain(key).And.NotContain(text);
    }

    [Test]
    public void Base64OfARepeatedPattern_IsRefused()
    {
        var zeros = Convert.ToBase64String(new byte[32]);
        var fifteenValues = Convert.ToBase64String(Enumerable.Range(0, 32).Select(x => (byte)(200 + (x % 15))).ToArray());
        var sixteenValues = Convert.ToBase64String(Enumerable.Range(0, 32).Select(x => (byte)(200 + (x % 16))).ToArray());

        EncryptionKeyRequirements.Problem(zeros).Should().Contain("not random");
        EncryptionKeyRequirements.Problem(fifteenValues).Should().Contain("not random");
        EncryptionKeyRequirements.Problem(sixteenValues).Should().BeNull($"{EncryptionKeyRequirements.MinimumDistinctByteValues} distinct values pass");
    }

    [Test]
    public void TheTestSuitesKey_IsAcceptable()
    {
        EncryptionKeyRequirements.Problem(Beacon.Tests.Common.TestEncryptionKey.Value).Should().BeNull();
    }

    [Test]
    public void PreviousEncryptionKeys_GivenAsASingleValue_IsReadAsOneKey()
    {
        const string oldKey = "an earlier passphrase key";
        var written = new EncryptionService(oldKey).Encrypt("Host=db;Password=old");

        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Beacon:EncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Beacon:PreviousEncryptionKeys"] = oldKey
        });

        provider.GetRequiredService<IEncryptionService>().Decrypt(written).Should().Be("Host=db;Password=old");
    }

    [Test]
    public void Startup_RefusesAWeakKey_WithoutNamingIt()
    {
        var act = () => BuildProvider(new Dictionary<string, string?> { ["Beacon:EncryptionKey"] = "short-passphrase" });

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Beacon:EncryptionKey must be base64").And.NotContain("short-passphrase");
    }

    [Test]
    public void Startup_RefusesThePlaceholder()
    {
        var act = () => BuildProvider(new Dictionary<string, string?> { ["Beacon:EncryptionKey"] = ServiceConfiguration.DefaultEncryptionKey });

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("publicly known");
    }

    [Test]
    public void Startup_WithAPreviousKey_ReadsWhatTheOldKeyWrote_AndWritesWithTheNewOne()
    {
        const string oldKey = "an earlier passphrase key";
        var newKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var written = new EncryptionService(oldKey).Encrypt("Host=db;Password=old");

        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Beacon:EncryptionKey"] = newKey,
            ["Beacon:PreviousEncryptionKeys:0"] = oldKey
        });
        var encryption = provider.GetRequiredService<IEncryptionService>();

        encryption.Decrypt(written).Should().Be("Host=db;Password=old");
        var rewritten = encryption.Encrypt("Host=db;Password=old");
        new EncryptionService(newKey).Decrypt(rewritten).Should().Be("Host=db;Password=old");
        var oldKeyOnly = () => new EncryptionService(oldKey).Decrypt(rewritten);
        oldKeyOnly.Should().Throw<CryptographicException>("a previous key is never used to encrypt");
    }

    // By design: Beacon:PreviousEncryptionKeys is decrypt-only, so a weak or published key is accepted there. It keeps the
    // values it wrote readable until they are re-encrypted, and nothing new is written with it.
    [Test]
    public void APublishedOrWeakPreviousKey_IsAccepted_ForReadingOnly()
    {
        var written = new EncryptionService(ServiceConfiguration.DefaultEncryptionKey).Encrypt("Host=db;Password=old");

        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Beacon:EncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Beacon:PreviousEncryptionKeys:0"] = ServiceConfiguration.DefaultEncryptionKey,
            ["Beacon:PreviousEncryptionKeys:1"] = "short"
        });
        var encryption = provider.GetRequiredService<IEncryptionService>();

        encryption.Decrypt(written).Should().Be("Host=db;Password=old");
        var rewritten = encryption.Encrypt("Host=db;Password=old");
        var withThePublishedKey = () => new EncryptionService(ServiceConfiguration.DefaultEncryptionKey).Decrypt(rewritten);
        withThePublishedKey.Should().Throw<CryptographicException>("a previous key is never used to encrypt");
    }

    // Every value the README, the documentation, the agent guides and the sample host's settings assign to the encryption
    // key (or a previous key) is refused: a sample value printed there is public. Failures name the place, never the value.
    [Test]
    public void EveryKeyValueTheDocumentationShows_IsRefused()
    {
        var root = RepositoryRoot();
        var files = new[] { "README.md", "CLAUDE.md" }
            .Select(x => Path.Combine(root, x))
            .Where(File.Exists)
            .Concat(Files(Path.Combine(root, "docs"), "*.md", "*.mdx"))
            .Concat(Files(Path.Combine(root, ".claude"), "*.md"))
            .Concat(Files(Path.Combine(root, "src", "Beacon.SampleProject"), "appsettings*.json"))
            .ToList();
        var assignment = new System.Text.RegularExpressions.Regex(
            @"(?:EncryptionKey|PreviousEncryptionKeys(?:(?:__|:)\d+)?)(?:""\]?\s*[:=]\s*""|""\s+""|'\s+'|\s*=\s*[""']?|""?\s*:\s+[""']?)(?<value>[^""'\s]+)");

        var found = 0;
        var accepted = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (System.Text.RegularExpressions.Match match in assignment.Matches(lines[i]))
                {
                    found++;
                    if (EncryptionKeyRequirements.Problem(match.Groups["value"].Value) == null)
                    {
                        accepted.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
                    }
                }
            }
        }

        found.Should().BePositive("the README and the installation guide show how the key is set");
        accepted.Should().BeEmpty("a key value printed in the repository is public: replace it with a generated one, or add its digest to the published keys");
    }

    private static IEnumerable<string> Files(string directory, params string[] patterns)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return patterns
            .SelectMany(x => Directory.EnumerateFiles(directory, x, SearchOption.AllDirectories))
            .Where(x => !x.Split(Path.DirectorySeparatorChar).Any(y => y is "node_modules" or "bin" or "obj" or "dist"));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Beacon.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests run inside the repository");

        return directory!.FullName;
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>())
            .UsePostgreSql("Host=localhost;Database=unused;Username=unused;Password=unused");

        return services.BuildServiceProvider();
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
