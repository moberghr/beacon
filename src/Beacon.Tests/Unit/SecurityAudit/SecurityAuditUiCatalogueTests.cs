using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.SecurityAudit;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The security audit page's filters offer the server's catalogue (<c>src/Beacon.UI/web/src/routes/security-audit/catalogue.ts</c>):
/// every action the server can record, every category and every resource type is listed there, so an event added on the
/// server cannot be left out of the Auditor's filters unnoticed.
/// </summary>
[TestFixture]
public class SecurityAuditUiCatalogueTests
{
    [Test]
    public void TheUiCatalogue_ListsEveryActionCategoryAndResourceType()
    {
        var catalogue = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Beacon.UI", "web", "src", "routes", "security-audit", "catalogue.ts"));

        var missing = SecurityAuditActions.All
            .Concat(Constants(typeof(SecurityAuditCategories)))
            .Concat(Constants(typeof(SecurityAuditResources)))
            .Where(x => !catalogue.Contains($"'{x}'", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty("the security audit page offers every action, category and resource type the server records");
    }

    [Test]
    public void TheUiCatalogue_NamesEveryActorKind()
    {
        var catalogue = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Beacon.UI", "web", "src", "routes", "security-audit", "catalogue.ts"));

        var missing = Constants(typeof(SecurityAuditActorKinds))
            .Where(x => !catalogue.Contains($"  {x}: '", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty("the security audit page names every kind of actor the server records");
    }

    private static IEnumerable<string> Constants(Type type)
    {
        return type
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.IsLiteral)
            .Select(x => (string)x.GetRawConstantValue()!);
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
