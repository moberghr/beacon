using Beacon.Api;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class PublicSchemaStartupWarningTests
{
    [Test]
    public void PublicSchemaOpenToEveryRole_IsWarnedAbout()
    {
        var logs = new LogRecorder();

        new PublicSchemaStartupWarning(logs.For<PublicSchemaStartupWarning>()).Report(publicCanCreate: true);

        logs.Entries.Should().ContainSingle()
            .Which.Level.Should().Be(LogLevel.Warning);
        logs.Entries[0].Message.Should().Contain("REVOKE CREATE ON SCHEMA public FROM PUBLIC");
    }

    [Test]
    public void PublicSchemaClosedToOtherRoles_LogsNothing()
    {
        var logs = new LogRecorder();

        new PublicSchemaStartupWarning(logs.For<PublicSchemaStartupWarning>()).Report(publicCanCreate: false);

        logs.Entries.Should().BeEmpty();
    }

    [Test]
    public async Task WithoutADatabase_TheCheckDoesNothing()
    {
        var logs = new LogRecorder();
        var services = new ServiceCollection().BuildServiceProvider();

        await new PublicSchemaStartupWarning(logs.For<PublicSchemaStartupWarning>()).CheckAsync(services, CancellationToken.None);

        logs.Entries.Should().BeEmpty();
    }
}
