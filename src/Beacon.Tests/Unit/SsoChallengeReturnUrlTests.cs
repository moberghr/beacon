using FluentAssertions;
using NUnit.Framework;
using Beacon.Api.Endpoints;
using Beacon.SampleProject.Authentication;

namespace Beacon.Tests.Unit;

[TestFixture]
public class SsoChallengeReturnUrlTests
{
    private const string BasePath = "/beacon";

    [TestCase("/beacon/dashboard")]
    [TestCase("/beacon")]
    [TestCase("/beacon/")]
    [TestCase("/BEACON/queries")]
    public void IsSafeReturnUrl_RelativePathUnderBase_IsAccepted(string url)
    {
        LoginEndpoints.IsSafeReturnUrl(url, BasePath).Should().BeTrue();
    }

    [TestCase("/projects")]
    [TestCase("/other-app/dashboard")]
    public void IsSafeReturnUrl_LocalPathOutsideBase_IsAccepted(string url)
    {
        LoginEndpoints.IsSafeReturnUrl(url, BasePath)
            .Should().BeTrue("UI routes live at the root post-cutover, so any local root-relative URL is a valid deep-link");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    [TestCase("https://evil.example/dashboard")]
    [TestCase("http://evil.example/dashboard")]
    [TestCase("//evil.example/dashboard")]
    [TestCase("/\\evil.example")]
    [TestCase("javascript:alert(1)")]
    [TestCase("dashboard")]
    public void IsSafeReturnUrl_UnsafeInput_IsRejected(string? url)
    {
        LoginEndpoints.IsSafeReturnUrl(url, BasePath).Should().BeFalse();
    }

    [Test]
    public void IsSafeReturnUrl_EmptyBasePath_AcceptsAnyRelativePath()
    {
        LoginEndpoints.IsSafeReturnUrl("/anywhere", string.Empty).Should().BeTrue();
        LoginEndpoints.IsSafeReturnUrl("//evil.example", string.Empty).Should().BeFalse();
    }
}
