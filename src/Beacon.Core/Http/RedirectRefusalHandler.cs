using System.Net;

namespace Beacon.Core.Http;

/// <summary>
/// For clients that never follow redirects: an answer that points somewhere else fails with a message that says so,
/// instead of surfacing as an unrelated status error.
/// </summary>
internal sealed class RedirectRefusalHandler : DelegatingHandler
{
    public const string Message = "The server answered with a redirect to another location; configure the final URL.";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"{Message} (HTTP {status})", null, response.StatusCode);
        }

        return response;
    }
}
