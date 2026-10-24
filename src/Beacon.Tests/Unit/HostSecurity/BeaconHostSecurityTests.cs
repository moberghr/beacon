using System.Net;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Api.Security;
using Beacon.Core.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Beacon.Tests.Unit.HostSecurity;

/// <summary>
/// The security response headers and the Host and Origin check on Beacon's routes (<c>Beacon:Security</c>): a minimal host
/// with Beacon's routes, an additional path (<c>/warp</c>) and the host's own routes, the two middlewares as
/// <c>UseBeaconAuthentication</c> adds them, and the options bound by <c>AddBeaconApiServices</c>.
/// </summary>
[TestFixture]
public class BeaconHostSecurityTests
{
    private const string EnforceGuard = "Enforce";

    [Test]
    public async Task BeaconRoute_CarriesTheSecurityHeadersAndThePolicy()
    {
        await using var host = await SecurityHost.StartAsync();

        var response = await host.Client.GetAsync("/beacon/queries");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Header(response, "X-Content-Type-Options").Should().Be("nosniff");
        Header(response, "X-Frame-Options").Should().Be("DENY");
        Header(response, "Referrer-Policy").Should().Be("strict-origin");
        Header(response, "Permissions-Policy").Should().Contain("camera=()");
        Header(response, "Content-Security-Policy").Should().Be(SecurityHeadersMiddleware.ContentSecurityPolicy);
        Header(response, "Content-Security-Policy-Report-Only").Should().BeNull();
    }

    [Test]
    public async Task BeaconApiError_CarriesTheSecurityHeaders()
    {
        await using var host = await SecurityHost.StartAsync();

        var response = await host.Client.GetAsync("/beacon/api/missing");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Header(response, "X-Content-Type-Options").Should().Be("nosniff");
        Header(response, "Content-Security-Policy").Should().NotBeNull();
    }

    [Test]
    public async Task AdditionalPath_CarriesTheHeadersButNoPolicy()
    {
        await using var host = await SecurityHost.StartAsync();

        var response = await host.Client.GetAsync("/warp/jobs");

        Header(response, "X-Content-Type-Options").Should().Be("nosniff");
        Header(response, "X-Frame-Options").Should().Be("DENY");
        Header(response, "Content-Security-Policy").Should().BeNull();
        Header(response, "Content-Security-Policy-Report-Only").Should().BeNull();
    }

    [Test]
    public async Task HostRoute_CarriesNoBeaconHeader()
    {
        await using var host = await SecurityHost.StartAsync();

        var response = await host.Client.GetAsync("/api/orders");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Header(response, "X-Content-Type-Options").Should().BeNull();
        Header(response, "X-Frame-Options").Should().BeNull();
        Header(response, "Content-Security-Policy").Should().BeNull();
    }

    [Test]
    public async Task ReportOnlyMode_SendsThePolicyAsReportOnly()
    {
        await using var host = await SecurityHost.StartAsync(new() { ["Beacon:Security:Csp"] = "ReportOnly" });

        var response = await host.Client.GetAsync("/beacon/queries");

        Header(response, "Content-Security-Policy").Should().BeNull();
        Header(response, "Content-Security-Policy-Report-Only").Should().Be(SecurityHeadersMiddleware.ContentSecurityPolicy);
    }

    [Test]
    public async Task OffMode_SendsNoPolicyButTheOtherHeaders()
    {
        await using var host = await SecurityHost.StartAsync(new() { ["Beacon:Security:Csp"] = "Off" });

        var response = await host.Client.GetAsync("/beacon/queries");

        Header(response, "Content-Security-Policy").Should().BeNull();
        Header(response, "Content-Security-Policy-Report-Only").Should().BeNull();
        Header(response, "X-Content-Type-Options").Should().Be("nosniff");
    }

    [Test]
    public async Task HeaderTheEndpointSet_IsKept()
    {
        await using var host = await SecurityHost.StartAsync();

        var response = await host.Client.GetAsync("/beacon/api/own-headers");

        Header(response, "Content-Security-Policy").Should().Be("default-src 'none'");
        Header(response, "X-Frame-Options").Should().Be("SAMEORIGIN");
        Header(response, "X-Content-Type-Options").Should().Be("nosniff");
    }

    [Test]
    public void Policy_AllowsNoScriptFromElsewhereAndNoFraming()
    {
        var directives = SecurityHeadersMiddleware.ContentSecurityPolicy
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToDictionary(x => x.Split(' ')[0], x => x[(x.IndexOf(' ') + 1)..]);

        directives["script-src"].Should().Be("'self'");
        directives["frame-ancestors"].Should().Be("'none'");
        directives["object-src"].Should().Be("'none'");
        directives["form-action"].Should().Be("'self'");
        directives["base-uri"].Should().Be("'self'");
        SecurityHeadersMiddleware.ContentSecurityPolicy.Should().NotContain("unsafe-eval");
        SecurityHeadersMiddleware.ContentSecurityPolicy.Should().NotContain("*");
    }

    [Test]
    public void SpaIndex_LoadsUnderThePolicy()
    {
        var uiDirectory = Path.Combine(RepositoryRoot(), "src", "Beacon.UI");
        var indexes = new List<string> { Path.Combine(uiDirectory, "web", "index.html") };
        var builtIndex = Path.Combine(uiDirectory, "wwwroot", "beacon", "index.html");
        if (File.Exists(builtIndex))
        {
            indexes.Add(builtIndex);
        }

        foreach (var index in indexes)
        {
            var html = File.ReadAllText(index);

            // script-src 'self': every script is a file, none inline, no inline handlers or a policy of its own.
            System.Text.RegularExpressions.Regex.Matches(html, "<script(?<attributes>[^>]*)>")
                .Select(x => x.Groups["attributes"].Value)
                .Should().NotBeEmpty()
                .And.OnlyContain(x => x.Contains("src=\"/"), index);
            html.Should().NotMatchRegex("\\son[a-z]+\\s*=", index);
            html.Should().NotContainEquivalentOf("http-equiv=\"Content-Security-Policy\"", index);

            // Scripts, styles and icons come from Beacon's own origin.
            System.Text.RegularExpressions.Regex.Matches(html, "(?:src|href)=\"(?<url>[^\"]+)\"")
                .Select(x => x.Groups["url"].Value)
                .Should().OnlyContain(x => x.StartsWith('/') && !x.StartsWith("//"), index);
        }
    }

    [Test]
    public void SpaBundle_NeedsNoEvalAndNoOtherOrigin()
    {
        var assets = Path.Combine(RepositoryRoot(), "src", "Beacon.UI", "wwwroot", "beacon", "assets");
        if (!Directory.Exists(assets))
        {
            Assert.Ignore("The SPA is not built in this checkout (ShouldBuildReact off).");
        }

        var scripts = Directory.GetFiles(assets, "*.js");
        scripts.Should().NotBeEmpty();
        foreach (var script in scripts)
        {
            var source = File.ReadAllText(script);
            source.Should().NotContain("new Function(", Path.GetFileName(script));
            source.Should().NotMatchRegex("(^|[^A-Za-z0-9_$.])eval\\(", Path.GetFileName(script));
        }

        foreach (var stylesheet in Directory.GetFiles(assets, "*.css"))
        {
            var source = File.ReadAllText(stylesheet);
            source.Should().NotMatchRegex("url\\(\\s*[\"']?(https?:)?//", Path.GetFileName(stylesheet));
            source.Should().NotContain("@import", Path.GetFileName(stylesheet));
        }
    }

    [Test]
    public async Task SameOriginPost_IsAllowed()
    {
        await using var host = await SecurityHost.StartAsync(new() { ["Beacon:Security:OriginGuard"] = EnforceGuard });

        var response = await host.SendAsync(HttpMethod.Post, "http://localhost/beacon/api/data", origin: "http://localhost");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestCase("https://other.example")]
    [TestCase("https://localhost")]
    [TestCase("http://localhost:8080")]
    [TestCase("null")]
    public async Task CrossOriginPost_IsRefused(string origin)
    {
        await using var host = await SecurityHost.StartAsync(new() { ["Beacon:Security:OriginGuard"] = EnforceGuard });

        var response = await host.SendAsync(HttpMethod.Post, "http://localhost/beacon/api/data", origin: origin);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [TestCase("cross-site", HttpStatusCode.Forbidden)]
    [TestCase("same-site", HttpStatusCode.Forbidden)]
    [TestCase("same-origin", HttpStatusCode.OK)]
    [TestCase("none", HttpStatusCode.OK)]
    public async Task PostWithoutOrigin_FollowsTheFetchSite(string fetchSite, HttpStatusCode expected)
    {
        await using var host = await SecurityHost.StartAsync(new() { ["Beacon:Security:OriginGuard"] = EnforceGuard });

        var response = await host.SendAsync(HttpMethod.Post, "http://localhost/beacon/api/data", fetchSite: fetchSite);

        response.StatusCode.Should().Be(expected);
    }

    [Test]
    public async Task PostFromAClientThatIsNoBrowser_IsAllowed()
    {
        await using var host = await SecurityHost.StartAsync(new() { ["Beacon:Security:OriginGuard"] = EnforceGuard });

        var response = await host.SendAsync(HttpMethod.Post, "http://localhost/beacon/api/data");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task CrossOriginReadOfAnAuthenticatedRoute_IsLeftToTheBrowser()
    {
        await using var host = await SecurityHost.StartAsync(new() { ["Beacon:Security:OriginGuard"] = EnforceGuard });

        var response = await host.SendAsync(HttpMethod.Get, "http://localhost/beacon/api/data", origin: "https://other.example", fetchSite: "cross-site");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task CrossOriginNavigationToAnAnonymousPage_IsAllowed()
    {
        await using var host = await SecurityHost.StartAsync(new() { ["Beacon:Security:OriginGuard"] = EnforceGuard });

        var response = await host.SendAsync(HttpMethod.Get, "http://localhost/beacon/api/anonymous", fetchSite: "cross-site");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestCase("/beacon/api/hub")]
    [TestCase("/beacon/api/hub/negotiate")]
    public async Task CrossOriginHubRequest_IsRefused(string path)
    {
        await using var host = await SecurityHost.StartAsync(new() { ["Beacon:Security:OriginGuard"] = EnforceGuard });

        var refused = await host.SendAsync(HttpMethod.Get, "http://localhost" + path, origin: "https://other.example");
        var sameOrigin = await host.SendAsync(HttpMethod.Get, "http://localhost" + path, origin: "http://localhost");

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        sameOrigin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task CrossOriginPostToAnAdditionalPath_IsRefused()
    {
        await using var host = await SecurityHost.StartAsync(new() { ["Beacon:Security:OriginGuard"] = EnforceGuard });

        var response = await host.SendAsync(HttpMethod.Post, "http://localhost/warp/api/recurring/1/disable", origin: "https://other.example");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task CrossOriginPostToAHostRoute_IsLeftToTheHost()
    {
        await using var host = await SecurityHost.StartAsync(new() { ["Beacon:Security:OriginGuard"] = EnforceGuard });

        var response = await host.SendAsync(HttpMethod.Post, "http://localhost/api/orders", origin: "https://other.example");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task LogOnlyMode_LetsARefusedRequestThroughAndLogsIt()
    {
        await using var host = await SecurityHost.StartAsync();

        var response = await host.SendAsync(HttpMethod.Post, "http://localhost/beacon/api/data", origin: "https://other.example");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Warnings.Should().ContainSingle()
            .Which.Should().Contain(nameof(OriginGuardVerdict.ForeignOrigin)).And.Contain("/beacon/api/data");
    }

    [Test]
    public async Task BehindAProxy_ThePublicBaseUrlIsTheAllowedOrigin()
    {
        // TLS ends at the proxy: the request arrives over http with the public Host, the browser's Origin is https.
        var settings = new Dictionary<string, string?>
        {
            ["Beacon:Security:OriginGuard"] = EnforceGuard,
            ["Beacon:PublicBaseUrl"] = "https://beacon.example.com/"
        };
        await using var proxied = await SecurityHost.StartAsync(settings);
        await using var unconfigured = await SecurityHost.StartAsync(new() { ["Beacon:Security:OriginGuard"] = EnforceGuard });

        var allowed = await proxied.SendAsync(HttpMethod.Post, "http://beacon.example.com/beacon/api/data", origin: "https://beacon.example.com");
        var withoutPublicBaseUrl = await unconfigured.SendAsync(HttpMethod.Post, "http://beacon.example.com/beacon/api/data", origin: "https://beacon.example.com");
        var foreign = await proxied.SendAsync(HttpMethod.Post, "http://beacon.example.com/beacon/api/data", origin: "https://other.example");

        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        withoutPublicBaseUrl.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        foreign.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task WithConfiguredOrigins_ABrowserOnAnotherHostIsRefused()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Beacon:Security:OriginGuard"] = EnforceGuard,
            ["Beacon:Security:AllowedOrigins:0"] = "https://beacon.example.com"
        };
        await using var host = await SecurityHost.StartAsync(settings);

        // The browser sees one origin (the other name), so Origin and Host agree; the Host is not Beacon's.
        var post = await host.SendAsync(HttpMethod.Post, "http://other.example/beacon/api/data", origin: "http://other.example");
        var anonymousRead = await host.SendAsync(HttpMethod.Get, "http://other.example/beacon/api/anonymous", fetchSite: "same-origin");
        var probe = await host.SendAsync(HttpMethod.Get, "http://10.0.0.5/beacon/api/anonymous");
        var ownHost = await host.SendAsync(HttpMethod.Get, "https://beacon.example.com/beacon/api/anonymous", fetchSite: "same-origin");

        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        anonymousRead.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        probe.StatusCode.Should().Be(HttpStatusCode.OK);
        ownHost.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task CrossOriginPostWithAnAuthorizationHeader_IsAllowed()
    {
        await using var host = await SecurityHost.StartAsync(new() { ["Beacon:Security:OriginGuard"] = EnforceGuard });

        var bearer = await host.SendAsync(HttpMethod.Post, "http://localhost/beacon/api/data", origin: "https://other.example", authorization: "Bearer token");
        var cookieOnly = await host.SendAsync(HttpMethod.Post, "http://localhost/beacon/api/data", origin: "https://other.example");

        bearer.StatusCode.Should().Be(HttpStatusCode.OK);
        cookieOnly.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task EnforceWithoutConfiguredOrigins_WarnsAtStartup()
    {
        await using var unconfigured = await SecurityHost.StartAsync(new() { ["Beacon:Security:OriginGuard"] = EnforceGuard });
        await using var configured = await SecurityHost.StartAsync(new()
        {
            ["Beacon:Security:OriginGuard"] = EnforceGuard,
            ["Beacon:PublicBaseUrl"] = "https://beacon.example.com"
        });
        await using var logOnly = await SecurityHost.StartAsync();

        unconfigured.Warnings.Should().ContainSingle().Which.Should().Contain("no origin is configured");
        configured.Warnings.Should().BeEmpty();
        logOnly.Warnings.Should().BeEmpty();
    }

    [Test]
    public async Task ConfiguredUnicodeHost_MatchesTheBrowsersPunycodeForm()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Beacon:Security:OriginGuard"] = EnforceGuard,
            ["Beacon:Security:AllowedOrigins:0"] = "https://b\u00fccher.example"
        };
        await using var host = await SecurityHost.StartAsync(settings);

        var response = await host.SendAsync(HttpMethod.Post, "https://xn--bcher-kva.example/beacon/api/data", origin: "https://xn--bcher-kva.example");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task ConfiguredOrigin_WithAPort_IsAllowed()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Beacon:Security:OriginGuard"] = EnforceGuard,
            ["Beacon:Security:AllowedOrigins:0"] = "https://beacon.example.com:8443"
        };
        await using var host = await SecurityHost.StartAsync(settings);

        var allowed = await host.SendAsync(HttpMethod.Post, "https://beacon.example.com:8443/beacon/api/data", origin: "https://beacon.example.com:8443");
        var otherPort = await host.SendAsync(HttpMethod.Post, "https://beacon.example.com:8443/beacon/api/data", origin: "https://beacon.example.com");

        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        otherPort.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestCase("/beacon/signin-oidc", true)]
    [TestCase("/BEACON/signin-oidc", true)]
    [TestCase("/beacon/signout-oidc", true)]
    [TestCase("/beacon/api/data", false)]
    public async Task OpenIdConnectPaths_AreRecognisedAsRemoteSignIn(string path, bool expected)
    {
        // Where the check runs before UseAuthentication (the MCP gateway), the identity provider's post must still pass.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication()
            .AddOpenIdConnect(x =>
            {
                x.Authority = "https://idp.example.test";
                x.ClientId = "beacon-test";
                x.CallbackPath = "/beacon/signin-oidc";
                x.SignedOutCallbackPath = "/beacon/signout-oidc";
            });
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = path;

        var remote = await OriginGuardMiddleware.IsRemoteSignInPathAsync(context);

        remote.Should().Be(expected);
    }

    [Test]
    public async Task WithoutOpenIdConnect_NoPathIsRemoteSignIn()
    {
        await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/signin-oidc";

        (await OriginGuardMiddleware.IsRemoteSignInPathAsync(context)).Should().BeFalse();
    }

    [TestCase("https://beacon.example.com", true)]
    [TestCase("https://beacon.example.com/", true)]
    [TestCase("http://localhost:5000", true)]
    [TestCase("https://beacon.example.com/app", false)]
    [TestCase("https://beacon.example.com?x=1", false)]
    [TestCase("ftp://beacon.example.com", false)]
    [TestCase("beacon.example.com", false)]
    [TestCase("*", false)]
    public void AllowedOrigins_AreValidated(string origin, bool valid)
    {
        var result = new BeaconHostSecurityOptionsValidator().Validate(
            null,
            new BeaconHostSecurityOptions { AllowedOrigins = [origin] });

        result.Succeeded.Should().Be(valid);
    }

    private static string? Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            return string.Join(",", values);
        }

        return response.Content.Headers.TryGetValues(name, out var contentValues)
            ? string.Join(",", contentValues)
            : null;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Beacon.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Beacon.slnx not found above the test directory.");
    }

    private sealed class SecurityHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly WarningCollector _warnings;

        private SecurityHost(WebApplication app, WarningCollector warnings)
        {
            _app = app;
            _warnings = warnings;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public IReadOnlyList<string> Warnings => _warnings.Messages;

        public static async Task<SecurityHost> StartAsync(Dictionary<string, string?>? settings = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var warnings = new WarningCollector();
            builder.Logging.AddProvider(warnings);
            var configuration = new Dictionary<string, string?> { ["Beacon:Authentication:AdditionalPaths:0"] = "/warp" };
            foreach (var setting in settings ?? [])
            {
                configuration[setting.Key] = setting.Value;
            }

            builder.Configuration.AddInMemoryCollection(configuration);

            // As AddBeaconServices binds it.
            builder.Services.AddOptions<AuthenticationPathOptions>()
                .Bind(builder.Configuration.GetSection(AuthenticationPathOptions.SectionName));
            builder.Services.AddSingleton<AuthenticationPathPrefixes>();
            builder.Services.AddBeaconApiServices(x => x.Realtime = false);

            var app = builder.Build();
            app.UseRouting();
            app.UseBeaconSecurityHeaders();
            app.UseBeaconOriginGuard();

            app.MapGet("/api/orders", () => "orders");
            app.MapPost("/api/orders", () => "ordered");
            app.MapGet("/beacon/api/data", () => "data");
            app.MapPost("/beacon/api/data", () => "saved");
            app.MapGet("/beacon/api/anonymous", () => "anonymous").AllowAnonymous();
            app.MapGet("/beacon/api/hub", () => "hub");
            app.MapGet("/beacon/api/hub/negotiate", () => "negotiate");
            app.MapGet("/beacon/api/own-headers", (HttpContext context) =>
            {
                context.Response.Headers.ContentSecurityPolicy = "default-src 'none'";
                context.Response.Headers.XFrameOptions = "SAMEORIGIN";
                return "own";
            });
            app.MapGet("/beacon/api/missing", () => Results.NotFound());
            app.MapGet("/beacon/{**page}", () => Results.Content("<!doctype html>", "text/html"));
            app.MapGet("/warp/{**page}", () => "warp");
            app.MapPost("/warp/{**page}", () => "warp");

            await app.StartAsync();

            return new SecurityHost(app, warnings);
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? origin = null, string? fetchSite = null, string? authorization = null)
        {
            using var request = new HttpRequestMessage(method, url);
            if (origin != null)
            {
                request.Headers.TryAddWithoutValidation("Origin", origin);
            }

            if (fetchSite != null)
            {
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);
            }

            if (authorization != null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }

            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class WarningCollector : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToList();
                }
            }
        }

        public ILogger CreateLogger(string categoryName)
        {
            return categoryName == typeof(OriginGuardMiddleware).FullName
                ? new Collector(this)
                : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        public void Dispose()
        {
        }

        private sealed class Collector(WarningCollector owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return logLevel >= LogLevel.Warning;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                lock (owner._messages)
                {
                    owner._messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
