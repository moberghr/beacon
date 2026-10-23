using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Models;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>A scan that hit one of its limits is completed but partial, and the project's repository list says so.</summary>
[TestFixture]
public class RepositoryPartialScanTests
{
    private const int ProjectId = 2;

    [Test]
    public void TheReasonsOfACompletedScan_ReadBackAsWritten()
    {
        var text = RepositoryScanNotes.Format(["only the first 3 of 7 files were scanned", "1 files over 1000 bytes were skipped"]);

        RepositoryScanNotes.PartialReasons(ScanStatus.Completed, text)
            .Should().Equal("only the first 3 of 7 files were scanned", "1 files over 1000 bytes were skipped");
    }

    [TestCase(ScanStatus.Completed, null)]
    [TestCase(ScanStatus.Failed, "Partial scan: only the first 3 of 7 files were scanned.")]
    [TestCase(ScanStatus.Failed, "GitHub API returned 404 for acme/ledger.")]
    public void AFullScan_OrAFailedOne_IsNotPartial(ScanStatus status, string? error)
    {
        RepositoryScanNotes.Format([]).Should().BeNull();
        RepositoryScanNotes.PartialReasons(status, error).Should().BeEmpty();
    }

    [Test]
    public async Task ARepositoryWhoseLastScanHitALimit_IsListedAsPartial_WithTheReasons()
    {
        var scannedAt = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        var capture = new SqlCapture()
            .ThenRow(ProjectId, "Finance", "desc", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), true)
            .ThenNoRows()
            .ThenRows(
                [10, "https://github.com/acme/ledger", (int)ScanStatus.Completed, scannedAt, 4, "Partial scan: only the first 3 of 7 files were scanned.", false],
                [11, "https://github.com/acme/billing", (int)ScanStatus.Completed, scannedAt, 9, DBNull.Value, false])
            .ThenScalar(false);
        var scope = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [ProjectId] = ProjectRole.Editor }, includesOpen: true, notOpen: []);
        var configuration = new BeaconConfiguration();
        configuration.EnableUserManagement();
        var handler = new GetProjectDetailHandler(
            capture.Factory(),
            TestProjectAccess.Evaluator(scope),
            new DatabaseProjectMembershipSource(configuration, capture.Factory()),
            SettingsProviderMock.Create().Object,
            Options.Create(new ProjectAuthorizationOptions()));

        var detail = (await handler.Handle(new GetProjectDetailQuery(ProjectId), CancellationToken.None)).Project!;

        var ledger = detail.Repositories.Single(x => x.Id == 10);
        ledger.ScanStatus.Should().Be("Completed");
        ledger.PartialScan.Should().BeTrue();
        ledger.PartialScanReasons.Should().Equal("only the first 3 of 7 files were scanned");
        var billing = detail.Repositories.Single(x => x.Id == 11);
        billing.PartialScan.Should().BeFalse();
        billing.PartialScanReasons.Should().BeEmpty();
    }
}
