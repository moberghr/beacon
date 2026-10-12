using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Api.Authentication;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The Data Protection key ring at startup, outside Development, over an in-memory key repository: a key still stored
/// without encryption (written before key encryption was turned on) is revoked and a new, encrypted key is created, logged
/// by key id and count only; a ring of encrypted keys is left alone; a host that does not generate keys, or a repository
/// that refuses the write, fails startup with what to do; Development changes nothing; no repository, or one acknowledged
/// as unencrypted, is a Warning.
/// </summary>
[TestFixture]
public class DataProtectionKeyRingStartupCheckTests
{
    private static readonly Lazy<X509Certificate2> Certificate = new(() =>
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=beacon-key-ring-tests", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    });

    [Test]
    public void AKeyStoredWithoutEncryption_IsRevoked_AndAnEncryptedKeyCreated_LoggedByIdOnly()
    {
        var repository = new MemoryRepository();
        var plaintext = CreatePlaintextKey(repository);
        var plaintextSecret = repository.SecretOf(plaintext);
        using var provider = EncryptedRing(repository);
        var logs = new LogRecorder();

        Check(logs).Inspect(provider);

        var keys = provider.GetRequiredService<IKeyManager>().GetAllKeys();
        keys.Single(x => x.KeyId == plaintext).IsRevoked.Should().BeTrue();
        var current = keys.Should().ContainSingle(x => !x.IsRevoked).Subject;
        repository.IsEncrypted(current.KeyId).Should().BeTrue("the new key is stored encrypted");
        var entry = logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Subject;
        entry.Message.Should().Contain("Revoked 1 Data Protection key(s)").And.Contain(plaintext.ToString()).And.Contain(current.KeyId.ToString());
        entry.Message.Should().NotContain(plaintextSecret, "key material never reaches the log");
    }

    [Test]
    public void ARingOfEncryptedKeys_IsLeftAlone()
    {
        var repository = new MemoryRepository();
        using var provider = EncryptedRing(repository);
        var keyManager = provider.GetRequiredService<IKeyManager>();
        keyManager.CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        var logs = new LogRecorder();

        Check(logs).Inspect(provider);

        keyManager.GetAllKeys().Should().ContainSingle().Which.IsRevoked.Should().BeFalse();
        logs.Entries.Should().BeEmpty();
    }

    [Test]
    public void AHostThatDoesNotGenerateKeys_FailsStartup_WithTheKeysToRevoke_AndNothingIsWritten()
    {
        var repository = new MemoryRepository();
        var plaintext = CreatePlaintextKey(repository);
        using var provider = EncryptedRing(repository, x => x.AutoGenerateKeys = false);
        var stored = repository.Count;

        var act = () => Check(new LogRecorder()).Inspect(provider);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{plaintext}*AutoGenerateKeys is off*Revoke them*");
        repository.Count.Should().Be(stored);
    }

    [Test]
    public void ARepositoryThatRefusesTheWrite_FailsStartup_WithWhatToDo()
    {
        var repository = new MemoryRepository();
        CreatePlaintextKey(repository);
        repository.ReadOnly = true;
        using var provider = EncryptedRing(repository);

        var act = () => Check(new LogRecorder()).Inspect(provider);

        act.Should().Throw<InvalidOperationException>().WithMessage("*could not revoke them*writable*");
    }

    [Test]
    public void InDevelopment_NothingIsChanged()
    {
        var repository = new MemoryRepository();
        var plaintext = CreatePlaintextKey(repository);
        using var provider = EncryptedRing(repository);

        Check(new LogRecorder(), Environments.Development).Inspect(provider);

        provider.GetRequiredService<IKeyManager>().GetAllKeys().Single(x => x.KeyId == plaintext).IsRevoked.Should().BeFalse();
    }

    [Test]
    public void WithoutAKeyRepository_StartupWarns()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        using var provider = services.BuildServiceProvider();
        var logs = new LogRecorder();

        Check(logs).Inspect(provider);

        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning && x.Message.StartsWith("No Data Protection key repository is configured", StringComparison.Ordinal));
    }

    [Test]
    public void AnAcknowledgedUnencryptedRing_StartsWithAWarning_AndItsKeysAreKept()
    {
        var repository = new MemoryRepository();
        var plaintext = CreatePlaintextKey(repository);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.Configure<KeyManagementOptions>(x => x.XmlRepository = repository);
        using var provider = services.BuildServiceProvider();
        var configuration = Acknowledged();
        var logs = new LogRecorder();

        var validation = new DataProtectionKeyEncryptionCheck(Environment(Environments.Production), configuration)
            .Validate(null, provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value);
        new DataProtectionKeyRingStartupCheck(logs.For<DataProtectionKeyRingStartupCheck>(), Environment(Environments.Production), configuration)
            .Inspect(provider);

        validation.Succeeded.Should().BeTrue();
        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning && x.Message.Contains(BeaconDataProtectionExtensions.AllowUnencryptedKeysKey));
        provider.GetRequiredService<IKeyManager>().GetAllKeys().Single(x => x.KeyId == plaintext).IsRevoked.Should().BeFalse();
    }

    [Test]
    public async Task AHostStartingOutsideDevelopment_RevokesTheKeysStoredWithoutEncryption()
    {
        var repository = new MemoryRepository();
        var plaintext = CreatePlaintextKey(repository);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddBeaconCookieAuthentication();
        builder.Services.AddDataProtection().ProtectKeysWithCertificate(Certificate.Value);
        builder.Services.Configure<KeyManagementOptions>(x => x.XmlRepository = repository);
        await using var app = builder.Build();

        await app.StartAsync();

        app.Services.GetRequiredService<IKeyManager>().GetAllKeys().Single(x => x.KeyId == plaintext).IsRevoked.Should().BeTrue();
        await app.StopAsync();
    }

    private static DataProtectionKeyRingStartupCheck Check(LogRecorder logs, string environmentName = "Production")
    {
        return new DataProtectionKeyRingStartupCheck(logs.For<DataProtectionKeyRingStartupCheck>(), Environment(environmentName));
    }

    // A key written the way a host without key encryption writes it.
    private static Guid CreatePlaintextKey(MemoryRepository repository)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.Configure<KeyManagementOptions>(x => x.XmlRepository = repository);
        using var provider = services.BuildServiceProvider();

        var key = provider.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(89));
        repository.IsEncrypted(key.KeyId).Should().BeFalse();

        return key.KeyId;
    }

    private static ServiceProvider EncryptedRing(MemoryRepository repository, Action<KeyManagementOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().ProtectKeysWithCertificate(Certificate.Value);
        services.Configure<KeyManagementOptions>(x =>
        {
            x.XmlRepository = repository;
            configure?.Invoke(x);
        });

        return services.BuildServiceProvider();
    }

    private static IHostEnvironment Environment(string environmentName)
    {
        return Mock.Of<IHostEnvironment>(x => x.EnvironmentName == environmentName);
    }

    private static IConfiguration Acknowledged()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [BeaconDataProtectionExtensions.AllowUnencryptedKeysKey] = "true" })
            .Build();
    }

    private sealed class MemoryRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];

        public bool ReadOnly { get; set; }

        public int Count => _elements.Count;

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            return _elements
                .Select(x => new XElement(x))
                .ToList();
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            if (ReadOnly)
            {
                throw new UnauthorizedAccessException("The key repository is read-only.");
            }

            _elements.Add(new XElement(element));
        }

        public bool IsEncrypted(Guid keyId)
        {
            return KeyElement(keyId).Descendants().Any(x => x.Name.LocalName == "encryptedSecret");
        }

        // The key's master key as stored without encryption.
        public string SecretOf(Guid keyId)
        {
            return KeyElement(keyId).Descendants().Single(x => x.Name.LocalName == "value").Value;
        }

        private XElement KeyElement(Guid keyId)
        {
            return _elements.Single(x => x.Name.LocalName == "key" && (string?)x.Attribute("id") == keyId.ToString());
        }
    }
}
