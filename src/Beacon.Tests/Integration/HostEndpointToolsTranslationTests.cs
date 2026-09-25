using Beacon.MCP.HostEndpoints;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Beacon.Tests.Integration;

/// <summary>Query-translation coverage (§4.3) for the host endpoint tools' project lookup.</summary>
[TestFixture]
public class HostEndpointToolsTranslationTests : QueryTranslationTestBase
{
    [Test]
    public void HostEndpointToolsProjectByName_Translates()
    {
        var sql = HostEndpointProjectResolver.BuildQuery(Context, "Netgiro").ToQueryString();

        sql.Should().Contain("projects");
        sql.Should().Contain(".name = ");
        sql.Should().Contain("archived_time IS NULL", "an archived project gives the tools no project");
    }
}
