using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.MCP.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Entra user callers provisioned with the default Viewer role read the catalog only: the host says so once at startup,
/// and only then.
/// </summary>
[TestFixture]
public class McpUserCallerStartupNoteTests
{
    [TestCase("Viewer", true, true, true, true)]
    [TestCase(" viewer ", true, true, true, true)]
    [TestCase("Editor", true, true, true, false)]
    [TestCase("Viewer", false, true, true, false)]
    [TestCase("Viewer", true, false, true, false)]
    [TestCase("Viewer", true, true, false, false)]
    public void ProvisionedUserCallersWithTheViewerRole_AreNotedOnceAtStartup(
        string defaultRoleName,
        bool usersEnabled,
        bool autoProvision,
        bool userManagement,
        bool noted)
    {
        var logs = new LogRecorder();
        var options = new McpCallerOptions
        {
            Users = new McpUserCallerOptions { Enabled = usersEnabled, AutoProvision = autoProvision, DefaultRoleName = defaultRoleName }
        };
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = userManagement;
        var note = new McpUserCallerStartupNote(Options.Create(options), logs.For<McpUserCallerStartupNote>(), configuration);
        Action<IApplicationBuilder> next = _ => { };

        var configured = note.Configure(next);

        configured.Should().BeSameAs(next, "the note changes nothing in the pipeline");
        if (noted)
        {
            logs.Entries.Should().ContainSingle()
                .Which.Should().Match<RecordedLog>(x => x.Level == LogLevel.Information && x.Message.Contains("need an Editor role"));
        }
        else
        {
            logs.Entries.Should().BeEmpty();
        }
    }
}
