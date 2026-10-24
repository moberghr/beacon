using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpLearning;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Models;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Beacon.Core.SecurityAudit;

using Beacon.Core.Services.Metadata;

namespace Beacon.Tests.Unit.Grounding;

/// <summary>
/// What a curator's documentation decisions change, once the route's Curate check passed: editing a section replaces its
/// content; applying a patch writes its proposed content to the column or table description or appends it to the project's
/// latest documentation section of that title, and records who applied it and when; rejecting one records who rejected it
/// and when and changes nothing else. A decided patch is not decided again, a patch whose target is gone stays
/// proposed, and a description patch whose target no longer holds what it was proposed on is refused (409) and stays
/// proposed. Real handlers over in-memory sets, no database (§4.7).
/// </summary>
[TestFixture]
public class DocumentationCurationEffectTests
{
    private const int ProjectId = 1;
    private const int DataSourceId = 10;
    private const int CuratorUserId = 42;
    private const string StatusDescription = "The order's state.";
    private const string OrdersDescription = "Orders.";

    [Test]
    public async Task EditingASection_ReplacesItsContent()
    {
        var store = new Store();

        await new UpdateDocumentationSectionHandler(store.Factory, new RecordingSecurityAudit())
            .Handle(new UpdateDocumentationSectionCommand(6, "Orders are placed by customers."), CancellationToken.None);

        store.Sections.Single(x => x.Id == 6).Content.Should().Be("Orders are placed by customers.");
        store.Sections.Single(x => x.Id == 7).Content.Should().Be("Older overview.", "only the edited section changes");
    }

    [Test]
    public async Task EditingASection_IsRecorded_BySectionAndProject_NeverItsContent()
    {
        var store = new Store();
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(CuratorUserId, "ext-curator"));
        const string content = "Orders are placed by customers.";

        await new UpdateDocumentationSectionHandler(store.Factory, audit)
            .Handle(new UpdateDocumentationSectionCommand(6, content), CancellationToken.None);

        var edited = audit.Single("Orders are placed");
        edited.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.DocumentationSectionEdited, ResourceType = SecurityAuditResources.DocumentationSection, ResourceId = "6", ProjectId = (int?)ProjectId },
            x => x.ExcludingMissingMembers());
        edited.DetailsJson.Should().Be($"{{\"documentationId\":5,\"contentLength\":{content.Length}}}");
        audit.Ways.Should().Equal("staged");
    }

    [Test]
    public async Task EditingAMissingSection_IsRefused()
    {
        var store = new Store();

        var act = () => new UpdateDocumentationSectionHandler(store.Factory, new RecordingSecurityAudit())
            .Handle(new UpdateDocumentationSectionCommand(99, "content"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Documentation section not found.");
    }

    [Test]
    public async Task ApplyingAColumnDescriptionPatch_WritesTheDescription_AndRecordsWhoApplied()
    {
        var store = new Store();
        var patch = store.Add(Patch(31, McpDocPatchTarget.ColumnDescription, "public.orders.status", "Where the order is in fulfilment."));
        var before = DateTime.UtcNow;

        await Apply(store, 31);

        store.Status.Description.Should().Be("Where the order is in fulfilment.");
        patch.Should().BeEquivalentTo(new { Status = McpDocPatchStatus.Applied, AppliedByUserId = (int?)CuratorUserId }, x => x.ExcludingMissingMembers());
        patch.AppliedAt.Should().BeOnOrAfter(before);
    }

    // A description is part of the stored catalog the ask path caches: applying one drops the source's snapshots.
    [TestCase(McpDocPatchTarget.ColumnDescription, "public.orders.status")]
    [TestCase(McpDocPatchTarget.TableDescription, "public.orders")]
    public async Task ApplyingADescriptionPatch_InvalidatesTheCatalogSnapshotsOfItsDataSource(McpDocPatchTarget target, string identifier)
    {
        var store = new Store();
        store.Add(Patch(31, target, identifier, "Curated text."));
        var snapshots = new Mock<ICatalogSnapshotCache>();

        await new ApplyDocumentationPatchHandler(store.Factory, new RecordingSecurityAudit(), snapshots.Object)
            .Handle(new ApplyDocumentationPatchCommand { PatchId = 31, AppliedByUserId = CuratorUserId }, CancellationToken.None);

        snapshots.Verify(x => x.Invalidate(DataSourceId), Times.Once);
    }

    [Test]
    public async Task ApplyingATableDescriptionPatch_WritesTheDescription()
    {
        var store = new Store();
        store.Add(Patch(31, McpDocPatchTarget.TableDescription, "public.orders", "Orders placed by customers."));

        await Apply(store, 31);

        store.Orders.TableDescription.Should().Be("Orders placed by customers.");
    }

    [Test]
    public async Task ApplyingADocumentationSectionPatch_AppendsToTheLatestDocumentationsSectionOfThatTitle()
    {
        var store = new Store();
        store.Add(Patch(31, McpDocPatchTarget.DocumentationSection, "Overview", "Refunds are orders with a negative total."));

        await Apply(store, 31);

        store.Sections.Single(x => x.Id == 6).Content.Should().Be("Current overview.\n\nRefunds are orders with a negative total.");
        store.Sections.Single(x => x.Id == 7).Content.Should().Be("Older overview.", "an older documentation's section is not written");
    }

    [Test]
    public async Task ApplyingAPatchWhoseTargetIsGone_LeavesItProposed()
    {
        var store = new Store();
        var patch = store.Add(Patch(31, McpDocPatchTarget.ColumnDescription, "public.orders.missing", "A column nobody has."));

        var act = () => Apply(store, 31);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Patch 31 target 'public.orders.missing' was not found; patch remains retryable");
        patch.Should().BeEquivalentTo(new { Status = McpDocPatchStatus.Proposed, AppliedByUserId = (int?)null, AppliedAt = (DateTime?)null }, x => x.ExcludingMissingMembers());
    }

    // Two projects sharing the data source each hold a patch proposed on the same description: the first applies, and the
    // second, proposed on content the column no longer holds, is refused and stays proposed.
    [Test]
    public async Task TwoPatchesProposedOnTheSameSharedDescription_TheSecondIsRefused_AndTheFirstsTextStays()
    {
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(CuratorUserId, "ext-curator"));
        var store = new Store();
        store.Add(Patch(31, McpDocPatchTarget.ColumnDescription, "public.orders.status", "Where the order is in fulfilment."));
        var second = store.Add(Patch(32, McpDocPatchTarget.ColumnDescription, "public.orders.status", "Shipping state.", projectId: 2));

        await Apply(store, 31, audit);
        var act = () => Apply(store, 32, audit);

        (await act.Should().ThrowAsync<BeaconConflictException>()).Which.Message.Should().Be(ApplyDocumentationPatchHandler.TargetChangedMessage);
        store.Status.Description.Should().Be("Where the order is in fulfilment.");
        second.Should().BeEquivalentTo(new { Status = McpDocPatchStatus.Proposed, AppliedByUserId = (int?)null, AppliedAt = (DateTime?)null }, x => x.ExcludingMissingMembers());
        audit.Events.Select(x => x.ResourceId).Should().Equal("31");
    }

    // The description no longer holds what the patch was proposed on, compared exactly: an edit by hand, a change of case
    // or of a trailing space, or an empty description where none was captured.
    [TestCase(McpDocPatchTarget.ColumnDescription, "public.orders.status", "Edited by hand.", StatusDescription)]
    [TestCase(McpDocPatchTarget.ColumnDescription, "public.orders.status", "the order's state.", StatusDescription)]
    [TestCase(McpDocPatchTarget.ColumnDescription, "public.orders.status", "The order's state. ", StatusDescription)]
    [TestCase(McpDocPatchTarget.ColumnDescription, "public.orders.status", "", null)]
    [TestCase(McpDocPatchTarget.TableDescription, "public.orders", "Edited by hand.", OrdersDescription)]
    [TestCase(McpDocPatchTarget.TableDescription, "public.orders", null, "")]
    public async Task APatchWhoseTargetChangedSinceItWasProposed_IsRefused_AndWritesNothing(
        McpDocPatchTarget target, string targetIdentifier, string? now, string? captured)
    {
        var audit = new RecordingSecurityAudit();
        var store = new Store();
        var patch = store.Add(Patch(31, target, targetIdentifier, "Proposed text."));
        patch.CurrentContent = captured;
        store.Status.Description = now;
        store.Orders.TableDescription = now;

        var act = () => Apply(store, 31, audit);

        (await act.Should().ThrowAsync<BeaconConflictException>()).Which.Message.Should().Be(ApplyDocumentationPatchHandler.TargetChangedMessage);
        store.Status.Description.Should().Be(now);
        store.Orders.TableDescription.Should().Be(now);
        patch.Should().BeEquivalentTo(new { Status = McpDocPatchStatus.Proposed, AppliedByUserId = (int?)null, AppliedAt = (DateTime?)null }, x => x.ExcludingMissingMembers());
        audit.Events.Should().BeEmpty();
    }

    // A patch proposed on a description that was absent applies while it still is.
    [TestCase(McpDocPatchTarget.ColumnDescription, "public.orders.status")]
    [TestCase(McpDocPatchTarget.TableDescription, "public.orders")]
    public async Task APatchProposedOnAnAbsentDescription_AppliesWhileItIsStillAbsent(McpDocPatchTarget target, string targetIdentifier)
    {
        var store = new Store();
        var patch = store.Add(Patch(31, target, targetIdentifier, "Proposed text."));
        patch.CurrentContent = null;
        store.Status.Description = null;
        store.Orders.TableDescription = null;

        await Apply(store, 31);

        (target == McpDocPatchTarget.ColumnDescription ? store.Status.Description : store.Orders.TableDescription).Should().Be("Proposed text.");
        patch.Status.Should().Be(McpDocPatchStatus.Applied);
    }

    [Test]
    public async Task RejectingAPatch_RecordsWhoRejected_AndWritesNothing()
    {
        var store = new Store();
        var patch = store.Add(Patch(31, McpDocPatchTarget.ColumnDescription, "public.orders.status", "Where the order is in fulfilment."));
        var before = DateTime.UtcNow;

        await new RejectDocumentationPatchHandler(store.Factory, new RecordingSecurityAudit())
            .Handle(new RejectDocumentationPatchCommand { PatchId = 31, RejectedByUserId = CuratorUserId }, CancellationToken.None);

        patch.Should().BeEquivalentTo(new { Status = McpDocPatchStatus.Rejected, AppliedByUserId = (int?)CuratorUserId }, x => x.ExcludingMissingMembers());
        patch.AppliedAt.Should().BeOnOrAfter(before);
        store.Status.Description.Should().Be("The order's state.");
    }

    [Test]
    public async Task ApplyingAndRejectingPatches_AreRecorded_NeverTheirText_AndARefusedDecisionIsNot()
    {
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(CuratorUserId, "ext-curator"));
        var store = new Store();
        store.Add(Patch(31, McpDocPatchTarget.ColumnDescription, "public.orders.status", "Where the order is in fulfilment."));
        store.Add(Patch(32, McpDocPatchTarget.ColumnDescription, "public.orders.status", "Shipping state."));

        await Apply(store, 31, audit);
        var again = () => Apply(store, 31, audit);
        await again.Should().ThrowAsync<InvalidOperationException>();
        await new RejectDocumentationPatchHandler(store.Factory, audit)
            .Handle(new RejectDocumentationPatchCommand { PatchId = 32, RejectedByUserId = CuratorUserId }, CancellationToken.None);

        audit.Events.Select(x => (x.Action, x.ResourceId)).Should().Equal(
            (SecurityAuditActions.DocumentationPatchApplied, "31"),
            (SecurityAuditActions.DocumentationPatchRejected, "32"));
        foreach (var auditEvent in audit.Events)
        {
            SecurityAuditTestKit.AssertNoPii(auditEvent, "fulfilment", "Shipping state", "public.orders.status");
            auditEvent.DetailsJson.Should().StartWith("{\"targetType\":\"ColumnDescription\",\"dataSourceId\":");
        }

        audit.Ways.Should().Equal("saved", "staged");
    }

    // A decision is made once: a decided patch is neither applied nor rejected again, and keeps who decided it.
    [TestCase(McpDocPatchStatus.Applied, true)]
    [TestCase(McpDocPatchStatus.Applied, false)]
    [TestCase(McpDocPatchStatus.Rejected, true)]
    [TestCase(McpDocPatchStatus.Rejected, false)]
    public async Task ADecidedPatch_IsNotDecidedAgain(McpDocPatchStatus status, bool apply)
    {
        var store = new Store();
        var patch = store.Add(Patch(31, McpDocPatchTarget.ColumnDescription, "public.orders.status", "Where the order is in fulfilment."));
        patch.Status = status;
        patch.AppliedByUserId = 5;

        Func<Task> act = apply
            ? () => Apply(store, 31)
            : () => new RejectDocumentationPatchHandler(store.Factory, new RecordingSecurityAudit()).Handle(new RejectDocumentationPatchCommand { PatchId = 31, RejectedByUserId = CuratorUserId }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"Patch 31 is already {status}");
        patch.Should().BeEquivalentTo(new { Status = status, AppliedByUserId = (int?)5 }, x => x.ExcludingMissingMembers());
        store.Status.Description.Should().Be("The order's state.");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task DecidingAMissingPatch_IsRefused(bool apply)
    {
        var store = new Store();

        Func<Task> act = apply
            ? () => Apply(store, 99)
            : () => new RejectDocumentationPatchHandler(store.Factory, new RecordingSecurityAudit()).Handle(new RejectDocumentationPatchCommand { PatchId = 99 }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Patch 99 not found");
    }

    private static Task Apply(Store store, int patchId, ISecurityAuditRecorder? audit = null)
    {
        return new ApplyDocumentationPatchHandler(store.Factory, audit ?? new RecordingSecurityAudit())
            .Handle(new ApplyDocumentationPatchCommand { PatchId = patchId, AppliedByUserId = CuratorUserId }, CancellationToken.None);
    }

    // A patch proposed on the store's current content: the status column's and the orders table's descriptions.
    private static McpDocumentationPatch Patch(int id, McpDocPatchTarget target, string targetIdentifier, string proposed, int projectId = ProjectId)
    {
        return new McpDocumentationPatch
        {
            Id = id,
            ProjectId = projectId,
            DataSourceId = DataSourceId,
            TargetType = target,
            TargetIdentifier = targetIdentifier,
            CurrentContent = target switch
            {
                McpDocPatchTarget.ColumnDescription => StatusDescription,
                McpDocPatchTarget.TableDescription => OrdersDescription,
                _ => null
            },
            ProposedContent = proposed,
            Reasoning = "Asked about often.",
            Status = McpDocPatchStatus.Proposed
        };
    }

    /// <summary>
    /// Data source 10's <c>public.orders</c> (with its <c>status</c> column), project 1's latest documentation (5, its
    /// Overview section 6) and an older one (4, its Overview section 7), and the patches a test adds.
    /// </summary>
    private sealed class Store
    {
        private readonly List<McpDocumentationPatch> _patches = [];

        public Store()
        {
            Orders = new DatabaseMetadata { Id = 3, DataSourceId = DataSourceId, SchemaName = "public", TableName = "orders", TableDescription = OrdersDescription };
            Status = new ColumnMetadata { Id = 8, ColumnName = "status", DataType = "text", Description = StatusDescription, DatabaseMetadata = Orders };
            var id = new ColumnMetadata { Id = 9, ColumnName = "id", DataType = "integer", DatabaseMetadata = Orders };
            Orders.Columns = [id, Status];
            var latest = new ProjectDocumentation { Id = 5, ProjectId = ProjectId, GeneratedByModel = "model", GeneratedAt = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc) };
            var older = new ProjectDocumentation { Id = 4, ProjectId = ProjectId, GeneratedByModel = "model", GeneratedAt = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc) };
            Sections.Add(new ProjectDocumentationSection { Id = 6, ProjectDocumentationId = 5, Title = "Overview", Content = "Current overview.", Documentation = latest });
            Sections.Add(new ProjectDocumentationSection { Id = 7, ProjectDocumentationId = 4, Title = "Overview", Content = "Older overview.", Documentation = older });
            var saved = new List<object>();
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(McpDocumentationPatch)] = RecordingBeaconContext.MemorySet(_patches, saved),
                        [typeof(DatabaseMetadata)] = RecordingBeaconContext.MemorySet([Orders], saved),
                        [typeof(ColumnMetadata)] = RecordingBeaconContext.MemorySet([id, Status], saved),
                        [typeof(ProjectDocumentation)] = RecordingBeaconContext.MemorySet([latest, older], saved),
                        [typeof(ProjectDocumentationSection)] = RecordingBeaconContext.MemorySet(Sections, saved)
                    },
                    saved));
            Factory = factory.Object;
        }

        public DatabaseMetadata Orders { get; }

        public ColumnMetadata Status { get; }

        public List<ProjectDocumentationSection> Sections { get; } = [];

        public IDbContextFactory<BeaconContext> Factory { get; }

        public McpDocumentationPatch Add(McpDocumentationPatch patch)
        {
            _patches.Add(patch);

            return patch;
        }
    }
}
