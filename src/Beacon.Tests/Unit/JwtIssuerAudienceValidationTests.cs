using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Beacon.Api.Authentication;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Worker;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Issuer and audience validation fail closed: a host that turns on bearer tokens or the external login endpoint must
/// configure at least one issuer and one audience (and keep both checks on) or it does not start, and the provider
/// never downgrades a missing issuer or audience to "accept any".
/// </summary>
[TestFixture]
public class JwtIssuerAudienceValidationTests
{
    private const string Issuer = "https://idp.example.test";
    private const string Audience = "api://beacon";

    // Generated per run: a test-only HMAC key, never a real secret (§1.2).
    private static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    [Test]
    public void Validate_NoTokenFlowEnabled_Passes()
    {
        var options = new JwtAuthenticationOptions();

        options.Invoking(x => x.Validate()).Should().NotThrow();
    }

    [Test]
    public void Validate_FullyPinnedBearer_Passes()
    {
        Pinned().Invoking(x => x.Validate()).Should().NotThrow();
    }

    [Test]
    public void Validate_IssuerAndAudienceLists_Count()
    {
        var options = Pinned();
        options.Validation.ValidIssuer = null;
        options.Validation.ValidIssuers = [Issuer];
        options.Validation.ValidAudience = null;
        options.Validation.ValidAudiences = [Audience];

        options.Invoking(x => x.Validate()).Should().NotThrow();
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public void Validate_TokenFlowWithoutIssuer_FailsStartup(bool bearer, bool externalLogin)
    {
        var options = Pinned(bearer, externalLogin);
        options.Validation.ValidIssuer = null;

        options.Invoking(x => x.Validate())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*issuer*");
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public void Validate_TokenFlowWithoutAudience_FailsStartup(bool bearer, bool externalLogin)
    {
        var options = Pinned(bearer, externalLogin);
        options.Validation.ValidAudience = null;

        options.Invoking(x => x.Validate())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*audience*");
    }

    [Test]
    public void Validate_IssuerCheckTurnedOff_FailsStartup()
    {
        var options = Pinned();
        options.Validation.ValidateIssuer = false;

        options.Invoking(x => x.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*issuer*");
    }

    [Test]
    public void Validate_LifetimeCheckTurnedOff_FailsStartup()
    {
        var options = Pinned();
        options.Validation.ValidateLifetime = false;

        options.Invoking(x => x.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*lifetime*");
    }

    [TestCase(-1, false)]
    [TestCase(0, true)]
    [TestCase(300, true)]
    [TestCase(301, false)]
    [TestCase(3600, false)]
    public void Validate_ClockSkew_IsCappedAtFiveMinutes(int seconds, bool accepted)
    {
        var options = Pinned();
        options.Validation.ClockSkew = TimeSpan.FromSeconds(seconds);

        if (accepted)
        {
            options.Invoking(x => x.Validate()).Should().NotThrow();
        }
        else
        {
            options.Invoking(x => x.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*ClockSkew*");
        }
    }

    [TestCase("scope", true)]
    [TestCase("scp", true)]
    [TestCase("sub", false)]
    [TestCase("aud", false)]
    [TestCase(" nonce ", false)]
    [TestCase("roles", false)]
    public void Validate_AccessTokenClaim_MustNotBeAClaimIdTokensCarry(string claim, bool accepted)
    {
        var options = Pinned();
        options.AccessTokenClaim = claim;

        if (accepted)
        {
            options.Invoking(x => x.Validate()).Should().NotThrow();
        }
        else
        {
            options.Invoking(x => x.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*AccessTokenClaim*");
        }
    }

    [Test]
    public async Task Provider_ExpiredToken_IsRejected_EvenWhenTheOptionsTurnTheLifetimeCheckOff()
    {
        var options = Pinned();
        options.Validation.ValidateLifetime = false;
        options.Validation.ClockSkew = TimeSpan.Zero;
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var expired = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            Issuer,
            Audience,
            [new Claim("sub", "user-1")],
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1),
            signingCredentials: credentials));

        var result = await CreateProvider(options).ValidateTokenAsync(expired);

        result.Success.Should().BeFalse();
    }

    [Test]
    public void Validate_AudienceCheckTurnedOff_FailsStartup()
    {
        var options = Pinned();
        options.Validation.ValidateAudience = false;

        options.Invoking(x => x.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*audience*");
    }

    [Test]
    public void AddBeaconJwtAuthentication_ValidatesTheInstanceItRegisters()
    {
        var services = new ServiceCollection();

        var register = () => services.AddBeaconJwtAuthentication(x =>
        {
            x.EnableBearerAuthentication = true;
            x.Validation.SigningKey = SigningKey;
            x.Validation.ValidIssuer = Issuer;
        });

        register.Should().Throw<InvalidOperationException>().WithMessage("*audience*");
        services.Should().NotContain(x => x.ServiceType == typeof(JwtAuthenticationOptions));
    }

    [Test]
    public void AddBeaconServices_JwtExternalLoginWithoutIssuer_FailsStartup()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
                ["Beacon:EncryptionKey"] = Convert.ToBase64String(new byte[32])
            })
            .Build();

        var register = () => services.AddBeaconServices(configuration, x =>
        {
            x.AddBeaconScheduler<NoOpScheduler>();
            x.Authentication.Jwt = new JwtAuthenticationOptions
            {
                ExternalLoginEndpoint = "https://idp.example.test/login",
                Validation = new JwtValidationOptions { SigningKey = SigningKey, ValidAudience = Audience }
            };
        });

        register.Should().Throw<InvalidOperationException>().WithMessage("*issuer*");
    }

    [Test]
    public async Task Provider_WithNoIssuerConfigured_RejectsTokens_InsteadOfAcceptingAnyIssuer()
    {
        // Built directly, past Validate(): even then a missing issuer is not a downgrade to "any issuer".
        var options = Pinned();
        options.Validation.ValidIssuer = null;

        var result = await CreateProvider(options).ValidateTokenAsync(MintToken(Issuer, Audience));

        result.Success.Should().BeFalse();
    }

    [Test]
    public async Task Provider_WithNoAudienceConfigured_RejectsTokens_InsteadOfAcceptingAnyAudience()
    {
        var options = Pinned();
        options.Validation.ValidAudience = null;

        var result = await CreateProvider(options).ValidateTokenAsync(MintToken(Issuer, Audience));

        result.Success.Should().BeFalse();
    }

    [TestCase("https://other-idp.example.test", Audience)]
    [TestCase(Issuer, "api://another-app")]
    public async Task Provider_ForeignIssuerOrAudience_IsRejected(string issuer, string audience)
    {
        var result = await CreateProvider(Pinned()).ValidateTokenAsync(MintToken(issuer, audience));

        result.Success.Should().BeFalse();
    }

    [Test]
    public async Task Provider_PinnedIssuerAndAudience_AcceptsTheToken()
    {
        var result = await CreateProvider(Pinned()).ValidateTokenAsync(MintToken(Issuer, Audience));

        result.Success.Should().BeTrue();
    }

    private static JwtAuthenticationOptions Pinned(bool bearer = true, bool externalLogin = false)
    {
        return new JwtAuthenticationOptions
        {
            EnableBearerAuthentication = bearer,
            ExternalLoginEndpoint = externalLogin ? "https://idp.example.test/login" : null,
            Validation = new JwtValidationOptions
            {
                SigningKey = SigningKey,
                ValidIssuer = Issuer,
                ValidAudience = Audience
            }
        };
    }

    private static JwtExternalApiAuthenticationProvider CreateProvider(JwtAuthenticationOptions options)
    {
        return new JwtExternalApiAuthenticationProvider(
            new HttpClient(),
            options,
            NullLogger<JwtExternalApiAuthenticationProvider>.Instance);
    }

    private static string MintToken(string issuer, string audience)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer,
            audience,
            [new Claim("sub", "user-1")],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
