namespace Beacon.Api;

/// <summary>
/// For hosts that own sign-in (their own login page and auth cookie). The React shell then never
/// renders Beacon's login page or its Sign out action: a signed-out user gets a full-page redirect to
/// <see cref="LoginUrl"/> with the page they were on passed as <see cref="ReturnUrlParameter"/>.
/// </summary>
public sealed class ExternalLoginOptions
{
    /// <summary>The host's login page, e.g. <c>/Account/Login</c>.</summary>
    public required string LoginUrl { get; set; }

    /// <summary>The query-string parameter the host's login page reads the post-login destination from.</summary>
    public string ReturnUrlParameter { get; set; } = "returnUrl";
}
