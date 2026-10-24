using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace Beacon.Api.Authentication;

/// <summary>
/// The failure of a Beacon credential that was presented and not accepted (an API key, a bearer token), carried as the
/// <see cref="Microsoft.AspNetCore.Authentication.AuthenticateResult.Failure"/> of its scheme. It holds what the caller
/// is told — never the reason that was logged — and whether the request is answered with it there and then: an API key
/// always, a bearer token only on an API request (<see cref="BeaconApiRequest"/>), so a page continues anonymously to
/// the cookie and the login redirect. A scheme handler keeps it on the request (<see cref="Recorded"/>) and answers no
/// result instead, so the framework does not log every refused credential (Beacon's own logs throttle them).
/// </summary>
internal sealed class BeaconCredentialRefusal : Exception
{
    private BeaconCredentialRefusal(int statusCode, string detail, bool answersRequest, string? error, string? challenge, string? retryAfter)
        : base(detail)
    {
        StatusCode = statusCode;
        AnswersRequest = answersRequest;
        Error = error;
        Challenge = challenge;
        RetryAfter = retryAfter;
    }

    public int StatusCode { get; }

    /// <summary>Whether the request is answered with this refusal rather than continuing unauthenticated.</summary>
    public bool AnswersRequest { get; }

    /// <summary>The OAuth error code (RFC 6750) the problem body carries as <c>error</c>, if any.</summary>
    public string? Error { get; }

    /// <summary>The <c>WWW-Authenticate</c> value of the answer, if any.</summary>
    public string? Challenge { get; }

    /// <summary>The <c>Retry-After</c> value of the answer, if any.</summary>
    public string? RetryAfter { get; }

    /// <summary>The refusal a scheme handler recorded on this request, if any.</summary>
    public static BeaconCredentialRefusal? Recorded(HttpContext context) => context.Features.Get<BeaconCredentialRefusal>();

    /// <summary>Keeps the refusal of <paramref name="result"/> on the request and turns it into no result.</summary>
    public static AuthenticateResult Record(HttpContext context, AuthenticateResult result)
    {
        if (result.Failure is not BeaconCredentialRefusal refusal)
        {
            return result;
        }

        context.Features.Set(refusal);

        return AuthenticateResult.NoResult();
    }

    public static BeaconCredentialRefusal Unauthorized(string detail, bool answersRequest, string? error = null, string? challenge = null)
    {
        return new BeaconCredentialRefusal(StatusCodes.Status401Unauthorized, detail, answersRequest, error, challenge, retryAfter: null);
    }

    public static BeaconCredentialRefusal Unavailable(string detail, string error, string retryAfter)
    {
        return new BeaconCredentialRefusal(StatusCodes.Status503ServiceUnavailable, detail, answersRequest: true, error, challenge: null, retryAfter);
    }
}
