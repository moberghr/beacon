using Beacon.Api.Authentication;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class MiddlewarePathHelperTests
{
    [TestCase("/_content/Beacon.UI/app.bin")]
    [TestCase("/assets/index-1a2b.js")]
    [TestCase("/favicon.ico")]
    public void IsStaticOrFrameworkPath_StaticAssets_AreStatic(string path)
    {
        MiddlewarePathHelper.IsStaticOrFrameworkPath(path).Should().BeTrue();
    }

    [TestCase("/_blazor")]
    [TestCase("/_blazor/negotiate")]
    [TestCase("/_framework/blazor.web")]
    public void IsStaticOrFrameworkPath_BlazorPaths_AreNoLongerStatic(string path)
    {
        MiddlewarePathHelper.IsStaticOrFrameworkPath(path).Should().BeFalse();
    }
}
