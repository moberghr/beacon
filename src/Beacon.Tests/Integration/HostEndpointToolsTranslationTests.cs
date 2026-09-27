using Beacon.Core.HostData;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Beacon.Tests.Integration;

/// <summary>Query-translation coverage (§4.3) for the shared host project lookup (endpoint tools, data source, docs).</summary>
[TestFixture]
public class HostEndpointToolsTranslationTests : QueryTranslationTestBase
{
    [Test]
    public void HostProjectByKey_Translates()
    {
        var sql = HostProjectResolver.BuildFindQuery(Context, HostProjectResolver.KeyFor("Netgiro")).ToQueryString();

        sql.Should().Contain("projects");
        sql.Should().Contain(".host_managed_key = ", "the host project is found by its key, never by name");
        sql.Should().NotContain(".name = ");
        sql.Should().Contain("archived_time IS NULL", "an archived project gives the tools no project");
    }
}
