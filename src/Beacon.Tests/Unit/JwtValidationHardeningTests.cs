using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Beacon.Api.Authentication;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Token validation settings: the accepted algorithms and token types, the signing-key floor, https endpoints, how long
/// cached JWKS keys may be used while refreshes fail, and the provider an external login registers.
/// </summary>
[TestFixture]
public class JwtValidationHardeningTests
{
    private const string Issuer = "https://idp.example.test";
    private const string Audience = "api://beacon";
    private const string JwksEndpoint = "https://idp.example.test/keys";

    private static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    [Test]
    public void SigningKeyBelowTheFloor_FailsValidation()
    {
        var options = Bearer(x => x.SigningKey = new string('k', JwtValidationOptions.MinimumSigningKeyBytes - 1));

        options.Invoking(x => x.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*SigningKey*32 bytes*");
    }

    [Test]
    public void SigningKeyAtTheFloor_PassesValidation()
    {
        var options = Bearer(x => x.SigningKey = new string('k', JwtValidationOptions.MinimumSigningKeyBytes));

        options.Invoking(x => x.Validate()).Should().NotThrow();
    }

    [TestCase("http://idp.example.test/keys")]
    [TestCase("ftp://idp.example.test/keys")]
    [TestCase("/keys")]
    public void JwksEndpointThatIsNotHttps_FailsValidation(string endpoint)
    {
        var options = Bearer(x => x.JwksEndpoint = endpoint);

        options.Invoking(x => x.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*JwksEndpoint*https*");
    }

    [Test]
    public void ExternalLoginEndpointOverHttp_FailsValidation()
    {
        var options = Bearer(x => x.SigningKey = SigningKey);
        options.ExternalLoginEndpoint = "http://idp.example.test/login";

        options.Invoking(x => x.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*ExternalLoginEndpoint*https*");
    }

    [TestCase("http://localhost:5001/keys")]
    [TestCase("http://127.0.0.1/keys")]
    public void LoopbackHttpJwksEndpoint_PassesValidation_ButIsListedForTheStartupCheck(string endpoint)
    {
        var options = Bearer(x => x.JwksEndpoint = endpoint);

        options.Invoking(x => x.Validate()).Should().NotThrow();
        options.LoopbackHttpEndpoints().Should().Equal("Validation.JwksEndpoint");
    }

    [Test]
    public void StartupCheck_LoopbackHttpOutsideDevelopment_FailsStartup()
    {
        var options = Bearer(x => x.JwksEndpoint = "http://localhost:5001/keys");
        var check = new JwtEndpointStartupCheck(options, Environment(Environments.Production));

        check.Invoking(x => x.Check()).Should().Throw<InvalidOperationException>()
            .WithMessage("*Validation.JwksEndpoint*https*")
            .Which.Message.Should().NotContain("localhost");
    }

    [Test]
    public void StartupCheck_LoopbackHttpInDevelopment_Starts()
    {
        var options = Bearer(x => x.JwksEndpoint = "http://localhost:5001/keys");
        var check = new JwtEndpointStartupCheck(options, Environment(Environments.Development));

        check.Invoking(x => x.Check()).Should().NotThrow();
    }

    [Test]
    public void StartupCheck_HttpsEndpointsOutsideDevelopment_Starts()
    {
        var options = Bearer(x => x.JwksEndpoint = JwksEndpoint);
        var check = new JwtEndpointStartupCheck(options, Environment(Environments.Production));

        check.Invoking(x => x.Check()).Should().NotThrow();
    }

    [Test]
    public void AddBeaconJwtAuthentication_RegistersTheEndpointStartupCheck()
    {
        var services = new ServiceCollection();

        services.AddBeaconJwtAuthentication(x =>
        {
            x.EnableBearerAuthentication = true;
            x.Validation.SigningKey = SigningKey;
            x.Validation.ValidIssuer = Issuer;
            x.Validation.ValidAudience = Audience;
        });

        services.Should().ContainSingle(x => x.ImplementationType == typeof(JwtEndpointStartupCheck));
    }

    [TestCase("JWT")]
    [TestCase("at+jwt")]
    [TestCase("application/at+jwt")]
    [TestCase(null)]
    public async Task TokenOfAnAcceptedType_IsAccepted(string? type)
    {
        var result = await SymmetricProvider().ValidateTokenAsync(MintSymmetric(type));

        result.Success.Should().BeTrue();
    }

    [TestCase("logout+jwt")]
    [TestCase("secevent+jwt")]
    [TestCase("id_token+jwt")]
    public async Task TokenOfAnotherType_IsRefused(string type)
    {
        var result = await SymmetricProvider().ValidateTokenAsync(MintSymmetric(type));

        result.Success.Should().BeFalse();
        result.SigningKeysUnavailable.Should().BeFalse();
    }

    [Test]
    public async Task AsymmetricTokenFromTheJwks_IsAccepted()
    {
        using var rsa = RSA.Create(2048);
        var handler = new JwksHandler { Body = "{\"keys\":[" + RsaJwk(rsa, "k1") + "]}" };
        var provider = JwksProvider(handler, new FakeTimeProvider());
        var credentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "k1" }, SecurityAlgorithms.RsaSsaPssSha256);

        var result = await provider.ValidateTokenAsync(Mint(credentials));

        result.Success.Should().BeTrue();
    }

    [Test]
    public async Task CachedKeys_AreServedWhileRefreshesFail_UntilTheMaximumStaleness()
    {
        using var rsa = RSA.Create(2048);
        var handler = new JwksHandler { Body = "{\"keys\":[" + RsaJwk(rsa, "k1") + "]}" };
        var time = new FakeTimeProvider();
        var provider = JwksProvider(handler, time);
        var credentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256);
        (await provider.ValidateTokenAsync(Mint(credentials))).Success.Should().BeTrue();

        handler.Fail = true;
        time.Advance(JwksSigningKeyCache.RefreshInterval);
        var withinStaleness = await provider.ValidateTokenAsync(Mint(credentials));

        time.Advance(JwksSigningKeyCache.MaximumStaleness - JwksSigningKeyCache.RefreshInterval);
        var pastStaleness = await provider.ValidateTokenAsync(Mint(credentials));

        withinStaleness.Success.Should().BeTrue("the last good key set is kept while a refresh fails");
        pastStaleness.Success.Should().BeFalse();
        pastStaleness.SigningKeysUnavailable.Should().BeTrue("keys older than the maximum staleness are no longer trusted");
    }

    [Test]
    public async Task FetchFailureWithoutKeys_IsNotRetriedBeforeTheRetryInterval()
    {
        using var rsa = RSA.Create(2048);
        var handler = new JwksHandler { Body = "{\"keys\":[" + RsaJwk(rsa, "k1") + "]}", Fail = true };
        var time = new FakeTimeProvider();
        var provider = JwksProvider(handler, time);
        var credentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256);

        var first = await provider.ValidateTokenAsync(Mint(credentials));
        handler.Fail = false;
        var second = await provider.ValidateTokenAsync(Mint(credentials));
        var requestsDuringBackoff = handler.Requests;

        time.Advance(JwksSigningKeyCache.FailureRetryInterval);
        var afterBackoff = await provider.ValidateTokenAsync(Mint(credentials));

        first.SigningKeysUnavailable.Should().BeTrue();
        second.SigningKeysUnavailable.Should().BeTrue();
        requestsDuringBackoff.Should().Be(1, "a failed fetch is not repeated before the retry interval");
        afterBackoff.Success.Should().BeTrue();
        handler.Requests.Should().Be(2);
    }

    [Test]
    public void ExternalLogin_RegisteredAfterBeaconServices_IsTheHybridProvidersFallback()
    {
        var services = BeaconServices();

        services.AddBeaconJwtAuthentication(ExternalLogin);

        ResolveAuthenticationProvider(services, withUserManagement: true).Should().BeOfType<HybridAuthenticationProvider>();
    }

    [Test]
    public void ExternalLogin_RegisteredBeforeBeaconServices_IsTheHybridProvidersFallback()
    {
        var services = new ServiceCollection();
        services.AddBeaconJwtAuthentication(ExternalLogin);

        AddBeaconServices(services);

        ResolveAuthenticationProvider(services, withUserManagement: true).Should().BeOfType<HybridAuthenticationProvider>();
    }

    [Test]
    public void ExternalLogin_WithoutUserManagement_IsTheProviderAlone()
    {
        var services = BeaconServices();

        services.AddBeaconJwtAuthentication(ExternalLogin);

        ResolveAuthenticationProvider(services, withUserManagement: false).Should().BeOfType<JwtExternalApiAuthenticationProvider>();
    }

    [Test]
    public void BearerOnly_KeepsTheConfiguredAuthenticationProvider()
    {
        var services = BeaconServices();

        services.AddBeaconJwtAuthentication(x =>
        {
            x.EnableBearerAuthentication = true;
            x.Validation.SigningKey = SigningKey;
            x.Validation.ValidIssuer = Issuer;
            x.Validation.ValidAudience = Audience;
        });

        services
            .Where(x => x.ServiceType == typeof(IBeaconAuthenticationProvider))
            .Should()
            .ContainSingle()
            .Which.ImplementationType.Should().Be<DatabaseAuthenticationProvider>();
    }

    [Test]
    public async Task HybridProvider_SignsInternalUsersInWithoutCallingTheExternalLogin()
    {
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.AuthenticateInternalUserAsync("alice", "secret", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticationResult { Success = true, BeaconUserId = 7 });
        var external = new JwksHandler { Body = "{}" };
        var configuration = new BeaconConfiguration();
        configuration.EnableUserManagement();
        var jwtProvider = new JwtExternalApiAuthenticationProvider(
            new HttpClient(external),
            ExternalLoginOptions(),
            NullLogger<JwtExternalApiAuthenticationProvider>.Instance,
            configuration,
            users.Object);
        var services = BeaconServices();
        services.AddBeaconJwtAuthentication(ExternalLogin);
        var provider = ResolveAuthenticationProvider(services, users.Object, jwtProvider, configuration);

        var result = await provider.AuthenticateAsync("alice", "secret");

        result.Success.Should().BeTrue();
        result.BeaconUserId.Should().Be(7);
        external.Requests.Should().Be(0, "an internal user signs in before the external login is tried");
    }

    private static void ExternalLogin(JwtAuthenticationOptions options)
    {
        options.ExternalLoginEndpoint = "https://idp.example.test/login";
        options.Validation.SigningKey = SigningKey;
        options.Validation.ValidIssuer = Issuer;
        options.Validation.ValidAudience = Audience;
    }

    private static JwtAuthenticationOptions ExternalLoginOptions()
    {
        var options = new JwtAuthenticationOptions();
        ExternalLogin(options);

        return options;
    }

    private static IBeaconAuthenticationProvider ResolveAuthenticationProvider(IServiceCollection services, bool withUserManagement)
    {
        var configuration = new BeaconConfiguration();
        configuration.EnableUserManagement();
        var jwtProvider = new JwtExternalApiAuthenticationProvider(
            new HttpClient(),
            ExternalLoginOptions(),
            NullLogger<JwtExternalApiAuthenticationProvider>.Instance);

        return ResolveAuthenticationProvider(
            services,
            withUserManagement ? new Mock<IUserManagementService>().Object : null,
            jwtProvider,
            configuration);
    }

    // The registered descriptor, resolved against just the services it needs (the full Beacon graph needs a database).
    private static IBeaconAuthenticationProvider ResolveAuthenticationProvider(
        IServiceCollection services,
        IUserManagementService? users,
        JwtExternalApiAuthenticationProvider jwtProvider,
        BeaconConfiguration configuration)
    {
        var descriptor = services
            .Where(x => x.ServiceType == typeof(IBeaconAuthenticationProvider))
            .Should()
            .ContainSingle("exactly one password sign-in provider is registered")
            .Subject;
        descriptor.ImplementationFactory.Should().NotBeNull();

        var dependencies = new ServiceCollection();
        dependencies.AddSingleton(jwtProvider);
        dependencies.AddSingleton(configuration);
        if (users != null)
        {
            dependencies.AddSingleton(users);
        }

        using var provider = dependencies.BuildServiceProvider();

        return (IBeaconAuthenticationProvider)descriptor.ImplementationFactory!(provider);
    }

    private static ServiceCollection BeaconServices()
    {
        var services = new ServiceCollection();
        AddBeaconServices(services);

        return services;
    }

    private static void AddBeaconServices(IServiceCollection services)
    {
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:EncryptionKey"] = TestEncryptionKey.Value
            })
            .Build();

        services.AddBeaconServices(configuration, x =>
        {
            x.AddBeaconScheduler<NoOpScheduler>();
            x.EnableUserManagement();
        });
    }

    private static JwtAuthenticationOptions Bearer(Action<JwtValidationOptions> configure)
    {
        var options = new JwtAuthenticationOptions
        {
            EnableBearerAuthentication = true,
            Validation = new JwtValidationOptions { ValidIssuer = Issuer, ValidAudience = Audience }
        };
        configure(options.Validation);

        return options;
    }

    private static IHostEnvironment Environment(string name)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns(name);

        return environment.Object;
    }

    private static JwtExternalApiAuthenticationProvider SymmetricProvider()
    {
        return new JwtExternalApiAuthenticationProvider(
            new HttpClient(),
            Bearer(x => x.SigningKey = SigningKey),
            NullLogger<JwtExternalApiAuthenticationProvider>.Instance);
    }

    private static JwtExternalApiAuthenticationProvider JwksProvider(JwksHandler handler, TimeProvider time)
    {
        return new JwtExternalApiAuthenticationProvider(
            new HttpClient(handler),
            Bearer(x => x.JwksEndpoint = JwksEndpoint),
            NullLogger<JwtExternalApiAuthenticationProvider>.Instance,
            new JwksSigningKeyCache(time));
    }

    private static string MintSymmetric(string? type)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);

        return Mint(credentials, type, setType: true);
    }

    private static string Mint(SigningCredentials credentials, string? type = null, bool setType = false)
    {
        var header = new JwtHeader(credentials);
        if (setType)
        {
            header.Remove(JwtHeaderParameterNames.Typ);
            if (type != null)
            {
                header[JwtHeaderParameterNames.Typ] = type;
            }
        }

        var payload = new JwtPayload(
            Issuer,
            Audience,
            [new Claim("sub", "subject-1")],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(10));

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    private static string RsaJwk(RSA rsa, string keyId)
    {
        var parameters = rsa.ExportParameters(false);

        return $$"""{"kty":"RSA","use":"sig","kid":"{{keyId}}","n":"{{Base64UrlEncoder.Encode(parameters.Modulus)}}","e":"{{Base64UrlEncoder.Encode(parameters.Exponent)}}"}""";
    }

    private sealed class JwksHandler : HttpMessageHandler
    {
        public string Body { get; set; } = "{\"keys\":[]}";

        public bool Fail { get; set; }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var status = Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(Body) });
        }
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
