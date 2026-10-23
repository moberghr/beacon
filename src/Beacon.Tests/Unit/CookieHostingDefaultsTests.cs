using Beacon.Api.Authentication;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.SecurityAudit;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class CookieHostingDefaultsTests
{
    [TestCase(null, CookieSecurePolicy.SameAsRequest)]
    [TestCase("false", CookieSecurePolicy.SameAsRequest)]
    [TestCase("true", CookieSecurePolicy.Always)]
    public void CookieSecurePolicy_FollowsTheSecureAlwaysOption(string? value, CookieSecurePolicy expected)
    {
        var values = new Dictionary<string, string?>();
        if (value != null)
        {
            values[BeaconCookieSecurity.SecureAlwaysKey] = value;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(Mock.Of<ISecurityAuditRecorder>());
        services.AddLogging();
        services.AddBeaconCookieAuthentication();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);

        options.Cookie.SecurePolicy.Should().Be(expected);
        options.Cookie.HttpOnly.Should().BeTrue();
        options.Cookie.SameSite.Should().Be(SameSiteMode.Lax);
    }

    [TestCase("http://localhost:5000", false, true)]
    [TestCase("https://localhost:5001", false, false)]
    [TestCase("http://localhost:5000", true, false)]
    public void PlainHttpListener_WarnsOnlyWhenTheCookieIsNotForcedSecure(string address, bool secureAlways, bool warns)
    {
        var logger = new CapturingLogger<CookieTransportStartupWarning>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [BeaconCookieSecurity.SecureAlwaysKey] = secureAlways.ToString() })
            .Build();
        var environment = Mock.Of<IHostEnvironment>(x => x.EnvironmentName == Environments.Production);
        var check = new CookieTransportStartupWarning(logger, environment, configuration);
        var addresses = new ServerAddressesFeature();
        addresses.Addresses.Add(address);
        var features = new FeatureCollection();
        features.Set<IServerAddressesFeature>(addresses);
        var server = new Mock<IServer>();
        server.SetupGet(x => x.Features).Returns(features);
        var started = new CancellationTokenSource();
        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.SetupGet(x => x.ApplicationStarted).Returns(started.Token);
        var services = new ServiceCollection();
        services.AddSingleton(server.Object);
        services.AddSingleton(lifetime.Object);
        using var provider = services.BuildServiceProvider();
        var builder = new Mock<IApplicationBuilder>();
        builder.SetupGet(x => x.ApplicationServices).Returns(provider);

        check.Configure(_ => { })(builder.Object);
        started.Cancel();

        logger.Messages.Any(x => x.Contains(BeaconCookieSecurity.SecureAlwaysKey, StringComparison.Ordinal))
            .Should().Be(warns);
    }

    [Test]
    public void WarpDashboardGuard_RefusesAMissingFilter_AndPassesAGivenOne()
    {
        var filter = new object();

        WarpDashboardGuard.Require(filter).Should().BeSameAs(filter);

        var missing = () => WarpDashboardGuard.Require<object>(null, "/jobs");
        missing.Should().Throw<InvalidOperationException>().WithMessage("*'/jobs'*no authorization filter*");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(logLevel + ": " + formatter(state, exception));
        }
    }
}
