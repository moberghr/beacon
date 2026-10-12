using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Api.Authentication;
using Beacon.Core.Data;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Data Protection belongs to the host: the cookie authentication sets no application name and no key storage, so a host's
/// own setup stands whatever the registration order. <c>AddBeaconDataProtection</c> is Beacon's default (the key ring in
/// the Beacon database, a configured application name, required outside Development). Outside Development a key ring
/// persisted without key encryption fails startup; Development may.
/// </summary>
[TestFixture]
public class DataProtectionOwnershipTests
{
    private static readonly Lazy<X509Certificate2> Certificate = new(() =>
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=beacon-data-protection-tests", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    });

    [TestCase(true)]
    [TestCase(false)]
    public void TheCookieAuthentication_LeavesTheHostsDataProtectionAlone_WhateverTheOrder(bool hostFirst)
    {
        var services = Services(Environments.Production);
        var keys = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "beacon-tests-never-created", Guid.NewGuid().ToString("N")));
        if (hostFirst)
        {
            services.AddDataProtection().SetApplicationName("host-app").PersistKeysToFileSystem(keys).ProtectKeysWithCertificate(Certificate.Value);
        }

        services.AddBeaconCookieAuthentication();
        if (!hostFirst)
        {
            services.AddDataProtection().SetApplicationName("host-app").PersistKeysToFileSystem(keys).ProtectKeysWithCertificate(Certificate.Value);
        }

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator.Should().Be("host-app");
        var keyManagement = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        keyManagement.XmlRepository.Should().BeOfType<FileSystemXmlRepository>();
        keyManagement.XmlEncryptor.Should().BeOfType<CertificateXmlEncryptor>();
    }

    [Test]
    public void TheCookieAuthentication_ConfiguresNoKeyStorage()
    {
        var services = Services(Environments.Production);
        services.AddBeaconCookieAuthentication();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository
            .Should().BeNull("without a host setup the framework's default storage applies");
        provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator
            .Should().NotBe(BeaconDataProtectionExtensions.DevelopmentApplicationName);
    }

    [Test]
    public void BeaconsDefault_InDevelopment_WithoutAName_KeepsTheKeysInTheDatabase()
    {
        var services = Services(Environments.Development);
        services.AddBeaconDataProtection(Configuration(), Environment(Environments.Development));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator
            .Should().Be(BeaconDataProtectionExtensions.DevelopmentApplicationName);
        var keyManagement = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        keyManagement.XmlRepository.Should().BeOfType<EntityFrameworkCoreXmlRepository<BeaconContext>>();
        keyManagement.XmlEncryptor.Should().BeNull("Development may keep the keys unencrypted");
    }

    [Test]
    public void BeaconsDefault_OutsideDevelopment_RequiresAnApplicationName()
    {
        var act = () => Services(Environments.Production).AddBeaconDataProtection(Configuration(), Environment(Environments.Production));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{BeaconDataProtectionExtensions.ApplicationNameKey} must be set outside Development*");
    }

    [Test]
    public void BeaconsDefault_OutsideDevelopment_WithANameAndKeyEncryption_IsAccepted()
    {
        var services = Services(Environments.Production);
        services.AddBeaconDataProtection(Configuration("beacon-uat"), Environment(Environments.Production))
            .ProtectKeysWithCertificate(Certificate.Value);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator.Should().Be("beacon-uat");
        provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor.Should().BeOfType<CertificateXmlEncryptor>();
    }

    [Test]
    public void BeaconsDefault_OutsideDevelopment_WithoutKeyEncryption_IsRefused()
    {
        var services = Services(Environments.Production);
        services.AddBeaconDataProtection(Configuration("beacon-uat"), Environment(Environments.Production));

        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*ProtectKeysWith*");
    }

    [Test]
    public void AHostsUnencryptedRepository_OutsideDevelopment_IsRefused_AndSoIsTheNullEncryptor()
    {
        var keys = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "beacon-tests-never-created", Guid.NewGuid().ToString("N")));
        var withoutEncryptor = Services(Environments.Staging);
        withoutEncryptor.AddBeaconCookieAuthentication();
        withoutEncryptor.AddDataProtection().PersistKeysToFileSystem(keys);
        var withNullEncryptor = Services(Environments.Staging);
        withNullEncryptor.AddBeaconCookieAuthentication();
        withNullEncryptor.AddDataProtection().PersistKeysToFileSystem(keys);
        withNullEncryptor.Configure<KeyManagementOptions>(x => x.XmlEncryptor = new NullXmlEncryptor());

        using var first = withoutEncryptor.BuildServiceProvider();
        using var second = withNullEncryptor.BuildServiceProvider();

        var unencrypted = () => first.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        var nullEncrypted = () => second.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        unencrypted.Should().Throw<OptionsValidationException>();
        nullEncrypted.Should().Throw<OptionsValidationException>("the null encryptor writes the keys in clear");
    }

    [Test]
    public void TheCheck_WithoutAHostEnvironment_JudgesAsOutsideDevelopment()
    {
        var options = new KeyManagementOptions { XmlRepository = Mock.Of<IXmlRepository>() };

        new DataProtectionKeyEncryptionCheck().Validate(null, options).Failed.Should().BeTrue();
        new DataProtectionKeyEncryptionCheck(Environment(Environments.Development)).Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task AHostOutsideDevelopment_DoesNotStart_WithAnUnencryptedKeyRing()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Configuration[BeaconDataProtectionExtensions.ApplicationNameKey] = "beacon-production";
        builder.Services.AddBeaconCookieAuthentication();
        builder.Services.AddBeaconDataProtection(builder.Configuration, builder.Environment);
        await using var app = builder.Build();

        var act = () => app.StartAsync();

        await act.Should().ThrowAsync<OptionsValidationException>().WithMessage("*ProtectKeysWith*");
    }

    [Test]
    public async Task AHostInDevelopment_StartsWithBeaconsDefault()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Services.AddBeaconDataProtection(builder.Configuration, builder.Environment);
        await using var app = builder.Build();

        await app.StartAsync();

        await app.StopAsync();
    }

    // The integration hosts run outside Development with an in-memory key ring encrypted by a certificate of the run.
    [Test]
    public void TheIntegrationHostsKeyRing_IsEncrypted_AndRoundTrips()
    {
        var services = Services(Environments.Staging);
        services.AddBeaconCookieAuthentication();
        TestDataProtection.UseEncryptedInMemoryKeyRing(services);

        using var provider = services.BuildServiceProvider();
        var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("tests");

        protector.Unprotect(protector.Protect("payload")).Should().Be("payload");
        provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor.Should().BeOfType<CertificateXmlEncryptor>();
    }

    private static ServiceCollection Services(string environmentName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Environment(environmentName));

        return services;
    }

    private static IHostEnvironment Environment(string environmentName)
    {
        return Mock.Of<IHostEnvironment>(x => x.EnvironmentName == environmentName);
    }

    private static IConfiguration Configuration(string? applicationName = null)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [BeaconDataProtectionExtensions.ApplicationNameKey] = applicationName })
            .Build();
    }
}
