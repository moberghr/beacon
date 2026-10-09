using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Notifications;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>Host allow-list entries: exact hosts and <c>*.</c> wildcards, matched case-insensitively and without a trailing dot.</summary>
[TestFixture]
public class HostPatternTests
{
    [TestCase("hooks.slack.com", "hooks.slack.com", true)]
    [TestCase("hooks.slack.com", "HOOKS.Slack.COM", true)]
    [TestCase("hooks.slack.com", "hooks.slack.com.", true)]
    [TestCase("hooks.slack.com.", "hooks.slack.com", true)]
    [TestCase("hooks.slack.com", "xhooks.slack.com", false)]
    [TestCase("hooks.slack.com", "hooks.slack.com.example.org", false)]
    [TestCase("hooks.slack.com", "a.hooks.slack.com", false)]
    [TestCase("*.atlassian.net", "acme.atlassian.net", true)]
    [TestCase("*.atlassian.net", "a.b.atlassian.net", true)]
    [TestCase("*.atlassian.net", "atlassian.net", false)]
    [TestCase("*.atlassian.net", "acmeatlassian.net", false)]
    [TestCase("*.atlassian.net", "acme.atlassian.net.example.org", false)]
    [TestCase("*.atlassian.net", "", false)]
    public void Matches(string pattern, string host, bool expected)
    {
        HostPattern.Matches(pattern, host).Should().Be(expected);
    }

    [TestCase("hooks.example.com", true)]
    [TestCase("localhost", true)]
    [TestCase("*.example.com", true)]
    [TestCase("*.a.b.example.com", true)]
    [TestCase("xn--hks-cdd.example.com", true)]
    [TestCase("*.com", false)]
    [TestCase("*", false)]
    [TestCase("*.", false)]
    [TestCase("a.*.example.com", false)]
    [TestCase("https://hooks.example.com", false)]
    [TestCase("hooks.example.com/path", false)]
    [TestCase("hooks example.com", false)]
    [TestCase("-hooks.example.com", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void IsValid(string? pattern, bool expected)
    {
        HostPattern.IsValid(pattern).Should().Be(expected);
    }
}
