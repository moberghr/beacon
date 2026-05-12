using FluentAssertions;
using NUnit.Framework;
using Beacon.SampleProject.Authentication;

namespace Beacon.Tests.Unit;

/// <summary>
/// Complements <see cref="SsoChallengeReturnUrlTests"/> for the post-cutover deployment
/// where the React shell is mounted at the root URL (<c>basePath = ""</c>). The SSO tests
/// cover the legacy <c>/beacon</c> base path; these cover the bare-root path and the
/// URL-encoded-but-safe inputs the SSO suite does not exercise.
/// </summary>
[TestFixture]
public class LoginEndpointsTests
{
    private const string RootBasePath = "";

    [TestCase("/projects")]
    [TestCase("/queries/123")]
    [TestCase("/projects?tab=overview")]
    [TestCase("/queries/123#results")]
    public void IsSafeReturnUrl_RelativePathUnderRoot_IsAccepted(string url)
    {
        LoginEndpoints.IsSafeReturnUrl(url, RootBasePath)
            .Should().BeTrue("relative SPA paths under '/' must be honoured after the React cutover");
    }

    [TestCase("/queries/q%2Fwith-slash")]
    [TestCase("/projects/Hello%20World")]
    public void IsSafeReturnUrl_UrlEncodedSafePath_IsAccepted(string url)
    {
        LoginEndpoints.IsSafeReturnUrl(url, RootBasePath)
            .Should().BeTrue("URL-encoded slashes/spaces inside the path must not be rejected");
    }

    [Test]
    public void IsSafeReturnUrl_NullReturnUrl_IsRejected()
    {
        LoginEndpoints.IsSafeReturnUrl(null, RootBasePath)
            .Should().BeFalse("a missing return URL must fall back to the configured login redirect");
    }

    [TestCase("")]
    [TestCase("   ")]
    public void IsSafeReturnUrl_EmptyOrWhitespace_IsRejected(string url)
    {
        LoginEndpoints.IsSafeReturnUrl(url, RootBasePath)
            .Should().BeFalse("empty / whitespace must not satisfy the safe-redirect predicate");
    }

    [TestCase("//evil.com/path")]
    [TestCase("/\\evil.com/path")]
    public void IsSafeReturnUrl_ProtocolRelativeOrBackslash_IsRejected(string url)
    {
        LoginEndpoints.IsSafeReturnUrl(url, RootBasePath)
            .Should().BeFalse("protocol-relative and backslash-prefixed URLs are classic open-redirect vectors");
    }

    [TestCase("https://evil.com")]
    [TestCase("http://evil.com/projects")]
    [TestCase("https://evil.com/queries/123")]
    public void IsSafeReturnUrl_AbsoluteUrl_IsRejected(string url)
    {
        LoginEndpoints.IsSafeReturnUrl(url, RootBasePath)
            .Should().BeFalse("absolute URLs must never be honoured as post-login destinations");
    }
}
