using System.Net.Http.Json;
using Beacon.Api.Endpoints;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Beacon.Tests.Common;

/// <summary>
/// An anonymous antiforgery pair minted by a test host's own antiforgery service, for posting to an endpoint that
/// requires the token from callers who are not signed in yet (the login form): the cookie token goes in the
/// <c>Cookie</c> header and the request token in the SPA's header, as the React shell sends them.
/// </summary>
internal static class TestAntiforgery
{
    public static Task<HttpResponseMessage> PostAsJsonWithAntiforgeryAsync<T>(
        this HttpClient client,
        IServiceProvider services,
        string path,
        T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        var antiforgery = services.GetRequiredService<IAntiforgery>();
        var cookieName = services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name;
        var tokens = antiforgery.GetTokens(new DefaultHttpContext { RequestServices = services });
        request.Headers.Add("Cookie", $"{cookieName}={tokens.CookieToken}");
        request.Headers.Add(AntiforgeryEndpointFilter.SpaHeaderName, tokens.RequestToken);

        return client.SendAsync(request);
    }
}
