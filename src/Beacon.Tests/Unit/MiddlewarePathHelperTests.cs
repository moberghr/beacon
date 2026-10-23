using Beacon.Api.Authentication;
using Beacon.Core.Configuration;
using Microsoft.Extensions.Options;
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

    [TestCase("/beacon/_content/Beacon.UI/app.bin", "/beacon", true)]
    [TestCase("/_content/Beacon.UI/app.bin", "/beacon", true)]
    [TestCase("/beacon/pages/_content/app.bin", "/beacon", false)]
    [TestCase("/beacon/_content/app.bin", "", false)]
    public void IsStaticOrFrameworkPath_ContentPrefix_MustLeadThePath(string path, string basePath, bool expected)
    {
        MiddlewarePathHelper.IsStaticOrFrameworkPath(path, basePath).Should().Be(expected);
    }

    [TestCase("/beacon/api", "/api", true)]
    [TestCase("/beacon/api/projects", "/api", true)]
    [TestCase("/api/projects", "/api", false)]
    [TestCase("/setup", "/setup", false)]
    [TestCase("/beacon/API/projects", "/api", true)]
    [TestCase("/beacon/apix", "/api", false)]
    [TestCase("/beacon/x/api/projects", "/api", false)]
    [TestCase("/other/api/projects", "/api", false)]
    public void IsUnderSegment_MatchesWholeSegmentsUnderTheBasePath(string path, string segment, bool expected)
    {
        MiddlewarePathHelper.IsUnderSegment(path, "/beacon", segment).Should().Be(expected);
    }

    [TestCase("/app/api/orders", "/api", true)]
    [TestCase("/APP/setup", "/setup", true)]
    [TestCase("/app/apix", "/api", false)]
    [TestCase("/other/api/orders", "/api", false)]
    public void IsUnderSegment_AlsoMatchesUnderConfiguredAdditionalPrefixes(string path, string segment, bool expected)
    {
        var prefixes = new AuthenticationPathPrefixes(Options.Create(new AuthenticationPathOptions { AdditionalPaths = ["/app"] }));

        MiddlewarePathHelper.IsUnderSegment(path, "/beacon", segment, prefixes).Should().Be(expected);
    }

    [TestCase("/app/_content/Beacon.UI/app.bin", true)]
    [TestCase("/other/_content/app.bin", false)]
    public void IsStaticOrFrameworkPath_AlsoMatchesUnderConfiguredAdditionalPrefixes(string path, bool expected)
    {
        var prefixes = new AuthenticationPathPrefixes(Options.Create(new AuthenticationPathOptions { AdditionalPaths = ["/app"] }));

        MiddlewarePathHelper.IsStaticOrFrameworkPath(path, "/beacon", prefixes).Should().Be(expected);
    }
}
