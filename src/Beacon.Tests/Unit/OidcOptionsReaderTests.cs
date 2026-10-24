using Beacon.Core.Authentication;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// <c>Beacon:Authentication:Oidc</c> has one reader, <see cref="OidcAuthenticationOptions.FromConfiguration"/>: the SSO
/// registration, the MCP discovery documents and the sample host's MCP bearer bridge all read the section through it.
/// </summary>
[TestFixture]
public class OidcOptionsReaderTests
{
    [Test]
    public void FromConfiguration_WithoutTheSection_GivesTheDefaults()
    {
        var options = OidcAuthenticationOptions.FromConfiguration(Configuration([]));

        options.Enabled.Should().BeFalse();
        options.CallbackPath.Should().Be("/signin-oidc");
        options.Scopes.Should().Equal("openid", "profile", "email");
        options.BlockGuests.Should().BeTrue();
        options.GetEnabledAuthority().Should().BeNull();
    }

    [Test]
    public void FromConfiguration_BindsTheSection()
    {
        var options = OidcAuthenticationOptions.FromConfiguration(Configuration(new()
        {
            [$"{OidcAuthenticationOptions.SectionName}:Enabled"] = "true",
            [$"{OidcAuthenticationOptions.SectionName}:Authority"] = "https://login.example.test/tenant/v2.0/",
            [$"{OidcAuthenticationOptions.SectionName}:ClientId"] = "beacon-client",
            [$"{OidcAuthenticationOptions.SectionName}:McpJwksEndpoint"] = "https://login.example.test/keys",
            [$"{OidcAuthenticationOptions.SectionName}:AllowedTenants:0"] = "tenant"
        }));

        options.Enabled.Should().BeTrue();
        options.ClientId.Should().Be("beacon-client");
        options.McpJwksEndpoint.Should().Be("https://login.example.test/keys");
        options.AllowedTenants.Should().Equal("tenant");
        options.GetEnabledAuthority().Should().Be("https://login.example.test/tenant/v2.0");
    }

    [Test]
    public void GetEnabledAuthority_WhileSsoIsOff_IsNull()
    {
        var options = OidcAuthenticationOptions.FromConfiguration(Configuration(new()
        {
            [$"{OidcAuthenticationOptions.SectionName}:Enabled"] = "false",
            [$"{OidcAuthenticationOptions.SectionName}:Authority"] = "https://login.example.test/"
        }));

        options.GetEnabledAuthority().Should().BeNull();
    }

    [Test]
    public void SectionName_IsTheDocumentedSection()
    {
        OidcAuthenticationOptions.SectionName.Should().Be("Beacon:Authentication:Oidc");
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
