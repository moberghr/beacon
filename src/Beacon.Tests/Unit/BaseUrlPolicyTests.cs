using Beacon.Core.Helpers;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class BaseUrlPolicyTests
{
    [TestCase("https://beacon.example", false)]
    [TestCase("https://beacon.example/app/", false)]
    [TestCase("  https://beacon.example  ", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    [TestCase("http://localhost:5000", true)]
    [TestCase("http://127.0.0.1:5000", true)]
    [TestCase("http://[::1]:5000", true)]
    public void AcceptedValues_Pass(string? value, bool development)
    {
        BaseUrlPolicy.Validate(value, development).Should().BeNull();
    }

    [TestCase("beacon.example", false)]
    [TestCase("/relative", false)]
    [TestCase("ftp://beacon.example", false)]
    [TestCase("javascript:alert(1)", false)]
    [TestCase("http://beacon.example", false)]
    [TestCase("http://beacon.example", true)]
    [TestCase("http://localhost:5000", false)]
    [TestCase("https://user:secret@beacon.example", false)]
    [TestCase("https://user@beacon.example", false)]
    [TestCase("https://beacon.example/#frag", false)]
    [TestCase("https://beacon.example#", false)]
    public void RejectedValues_AreRefused(string value, bool development)
    {
        BaseUrlPolicy.Validate(value, development).Should().Be(BaseUrlPolicy.InvalidMessage);
    }

    [TestCase("https://beacon.example/", "notifications/4", "https://beacon.example/notifications/4")]
    [TestCase("https://beacon.example/app", "notifications/4", "https://beacon.example/app/notifications/4")]
    [TestCase("not a url", "notifications/4", null)]
    [TestCase("https://user:pw@beacon.example", "notifications/4", null)]
    [TestCase("", "notifications/4", null)]
    [TestCase(null, "notifications/4", null)]
    public void Link_IsBuiltOnlyFromAUsableBaseUrl(string? baseUrl, string path, string? expected)
    {
        BaseUrlPolicy.Link(baseUrl, path).Should().Be(expected);
    }
}
