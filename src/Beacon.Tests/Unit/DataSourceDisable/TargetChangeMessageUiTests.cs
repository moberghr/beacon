using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Services;

namespace Beacon.Tests.Unit.DataSourceDisable;

/// <summary>
/// The replace-connection dialog offers the target-change confirmation only for the server's own 409 message
/// (<c>src/Beacon.UI/web/src/routes/data-sources/ReplaceConnectionDialog.tsx</c>), so the two texts must stay the same.
/// </summary>
[TestFixture]
public class TargetChangeMessageUiTests
{
    [Test]
    public void TheDialog_RecognisesTheServersTargetChangeMessage()
    {
        var dialog = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Beacon.UI", "web", "src", "routes", "data-sources", "ReplaceConnectionDialog.tsx"));

        dialog.Should().Contain(
            $"'{DataSourceService.TargetChangeUnconfirmedMessage}'",
            "the dialog asks for the confirmation only when the 409 carries this message");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Beacon.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests run inside the repository");

        return directory!.FullName;
    }
}
