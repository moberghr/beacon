using System.Data.Common;
using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Models;
using Beacon.MCP.Tools;

namespace Beacon.Tests.Unit;

/// <summary>
/// §1.11-adjacent — MCP tool failures must not leak internal exception detail to the caller.
/// Business-rule messages (§2.9) and database provider errors (the caller's own SQL feedback
/// loop) pass through; everything else maps to a generic, actionable message.
/// </summary>
[TestFixture]
public class ToolHelperCallerSafeMessageTests
{
    private sealed class FakeDbException(string message) : DbException(message);

    [Test]
    public void BusinessRuleException_PassesMessageThrough()
    {
        var message = ToolHelper.CallerSafeMessage(new InvalidOperationException("Folder not found."), "get_context");

        message.Should().Be("Folder not found.");
    }

    [Test]
    public void BeaconException_PassesMessageThrough()
    {
        var message = ToolHelper.CallerSafeMessage(new BeaconException("Data source is disabled."), "query");

        message.Should().Be("Data source is disabled.");
    }

    [Test]
    public void DbException_SurfacesProviderMessageForSelfCorrection()
    {
        var message = ToolHelper.CallerSafeMessage(new FakeDbException("column \"bogus\" does not exist"), "query");

        message.Should().StartWith("Query failed");
        message.Should().Contain("column \"bogus\" does not exist");
    }

    [Test]
    public void UnexpectedException_DoesNotLeakInternalDetail()
    {
        var message = ToolHelper.CallerSafeMessage(
            new ArgumentNullException("connectionString", "internal connection detail"), "search");

        message.Should().NotContain("internal connection detail");
        message.Should().NotContain("connectionString");
        message.Should().Contain("search");
    }
}
