using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpLearning;
using Beacon.Core.Handlers.Metadata;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Models;
using Beacon.Core.Services.Metadata;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Who curates grounding (<see cref="ProjectAction.Curate"/>): a project's own grounding — learned patterns, documentation,
/// a patch to a documentation section — needs Curate in the project; a data source's shared grounding — its relationships,
/// a patch to a column or table description — needs Write where the caller acts and Curate in every live project linking
/// the source, or a DataSourceAdmin, and a source no live project links is curated by a DataSourceAdmin or an Admin only.
/// The lists tell each row's caller the same answer, a relationship whose creator may not curate is stored unverified, and
/// a verified one that such a caller changes is stored unverified again.
/// The deployment: projects 1, 2 and 3; data source 10 linked to projects 1 and 2, 11 only to project 1, 12 to none.
/// </summary>
[TestFixture]
public class GroundingCurationAccessTests
{
    private const int SharedSource = 10;
    private const int OwnSource = 11;
    private const int UnlinkedSource = 12;
    private const int CuratorUserId = 42;

    private static readonly ProjectAccessScope AdminOfBoth = Roles((1, ProjectRole.ProjectAdmin), (2, ProjectRole.ProjectAdmin));
    private static readonly ProjectAccessScope AdminOfOne = Roles((1, ProjectRole.ProjectAdmin), (2, ProjectRole.Editor));
    private static readonly ProjectAccessScope EditorOfBoth = Roles((1, ProjectRole.Editor), (2, ProjectRole.Editor));
    private static readonly ProjectAccessScope Viewer = Roles((1, ProjectRole.Viewer));

    /// <summary>An Editor through open projects alone: the openly reachable sources, no membership.</summary>
    private static readonly ProjectAccessScope OpenEditor = TestProjectAccess.Scope(new Dictionary<int, ProjectRole>(), includesOpen: true, notOpen: []);

    [Test]
    public async Task APatternOrADocumentationSection_IsCuratedByItsProjectsAdmin_Alone()
    {
        var pattern = new ProjectCheck(ProjectResource.LearnedPattern(5), ProjectAction.Curate);
        var inProjectOne = ResolvedProjectResource.InProject(1);

        await Authorizer(AdminOfOne, inProjectOne).DemandAsync(pattern, CancellationToken.None);
        var editor = () => Authorizer(EditorOfBoth, inProjectOne).DemandAsync(pattern, CancellationToken.None);
        var viewer = () => Authorizer(Viewer, inProjectOne).DemandAsync(pattern, CancellationToken.None);
        var unreadable = () => Authorizer(AdminOfOne, ResolvedProjectResource.InProject(3)).DemandAsync(pattern, CancellationToken.None);

        await editor.Should().ThrowAsync<BeaconForbiddenException>();
        await viewer.Should().ThrowAsync<BeaconForbiddenException>();
        await unreadable.Should().ThrowAsync<BeaconNotFoundException>();
    }

    [Test]
    public async Task ASharedSourcesGrounding_NeedsCurateInEveryLinkingProject_OrADataSourceAdmin()
    {
        var verify = ProjectCheck.CurateShared(ProjectResource.DataSource(SharedSource));
        var source = ResolvedProjectResource.ThroughSources([SharedSource]);

        await Authorizer(AdminOfBoth, source).DemandAsync(verify, CancellationToken.None);
        await Authorizer(TestProjectAccess.Unrestricted, source).DemandAsync(verify, CancellationToken.None);
        await Authorizer(EditorOfBoth, source, TestPermissions.Holding(BeaconPermissions.DataSourceAdmin)).DemandAsync(verify, CancellationToken.None);
        var adminOfOne = () => Authorizer(AdminOfOne, source).DemandAsync(verify, CancellationToken.None);
        var viewerDataSourceAdmin = () => Authorizer(Viewer, source, TestPermissions.Holding(BeaconPermissions.DataSourceAdmin)).DemandAsync(verify, CancellationToken.None);

        (await adminOfOne.Should().ThrowAsync<BeaconForbiddenException>()).Which.Message.Should().Contain("project admin role in every project that links it");
        await viewerDataSourceAdmin.Should().ThrowAsync<BeaconForbiddenException>("a DataSourceAdmin still needs Write where it acts");
    }

    [Test]
    public async Task ASourceOnlyOneProjectLinks_IsCuratedByThatProjectsAdmin()
    {
        var verify = ProjectCheck.CurateShared(ProjectResource.DataSource(OwnSource));

        await Authorizer(AdminOfOne, ResolvedProjectResource.ThroughSources([OwnSource])).DemandAsync(verify, CancellationToken.None);
    }

    [Test]
    public async Task ASourceNoProjectLinks_IsCuratedByADataSourceAdminAlone_WhileWritingItStaysOpen()
    {
        var unlinked = ResolvedProjectResource.ThroughSources([UnlinkedSource]);
        var verify = ProjectCheck.CurateShared(ProjectResource.DataSource(UnlinkedSource));
        var write = ProjectCheck.SharedSource(ProjectResource.DataSource(UnlinkedSource), ProjectAction.Write);

        var openEditor = () => Authorizer(OpenEditor, unlinked).DemandAsync(verify, CancellationToken.None);
        await Authorizer(OpenEditor, unlinked, TestPermissions.Holding(BeaconPermissions.DataSourceAdmin)).DemandAsync(verify, CancellationToken.None);
        await Authorizer(OpenEditor, unlinked).DemandAsync(write, CancellationToken.None);

        await openEditor.Should().ThrowAsync<BeaconForbiddenException>("an open grant never curates");
    }

    [Test]
    public async Task APatchToADocumentationSection_NeedsCurateInItsProject_WhichNoPermissionReplaces()
    {
        var apply = ProjectCheck.CurateShared(ProjectResource.DocumentationPatch(8));
        var sectionPatch = ResolvedProjectResource.InProject(1);

        await Authorizer(AdminOfOne, sectionPatch).DemandAsync(apply, CancellationToken.None);
        var editor = () => Authorizer(EditorOfBoth, sectionPatch).DemandAsync(apply, CancellationToken.None);
        var dataSourceAdmin = () => Authorizer(EditorOfBoth, sectionPatch, TestPermissions.Holding(BeaconPermissions.DataSourceAdmin)).DemandAsync(apply, CancellationToken.None);

        (await editor.Should().ThrowAsync<BeaconForbiddenException>()).Which.Message.Should().Be("Curate is not allowed on this DocumentationPatch.");
        await dataSourceAdmin.Should().ThrowAsync<BeaconForbiddenException>();
    }

    [Test]
    public async Task APatchToADescription_NeedsCurateInEveryProjectLinkingItsSource_OrADataSourceAdmin()
    {
        var apply = ProjectCheck.CurateShared(ProjectResource.DocumentationPatch(8));
        var descriptionPatch = ResolvedProjectResource.InProject(1, [SharedSource]);

        await Authorizer(AdminOfBoth, descriptionPatch).DemandAsync(apply, CancellationToken.None);
        await Authorizer(EditorOfBoth, descriptionPatch, TestPermissions.Holding(BeaconPermissions.DataSourceAdmin)).DemandAsync(apply, CancellationToken.None);
        var adminOfOne = () => Authorizer(AdminOfOne, descriptionPatch).DemandAsync(apply, CancellationToken.None);

        await adminOfOne.Should().ThrowAsync<BeaconForbiddenException>();
    }

    [Test]
    public async Task APatchResolvesToSharedSourcesOnly_WhenItWritesADescription()
    {
        var store = new Store();
        store.Patches.Add(Patch(8, 1, SharedSource, McpDocPatchTarget.DocumentationSection));
        store.Patches.Add(Patch(9, 1, SharedSource, McpDocPatchTarget.ColumnDescription));
        await using var context = await store.Factory.CreateDbContextAsync();
        var resolver = new ProjectResourceResolver();

        var section = await resolver.ResolveAsync(context, ProjectResource.DocumentationPatch(8), CancellationToken.None);
        var column = await resolver.ResolveAsync(context, ProjectResource.DocumentationPatch(9), CancellationToken.None);

        section.Should().BeEquivalentTo(new { ProjectId = 1, SharedDataSourceIds = Array.Empty<int>() }, x => x.ExcludingMissingMembers());
        column.Should().BeEquivalentTo(new { ProjectId = 1, SharedDataSourceIds = new[] { SharedSource } }, x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task ThePatternList_TellsEachRowWhetherTheCallerCurates()
    {
        var store = new Store();
        store.Patterns.Add(Pattern(21, 1));
        store.Patterns.Add(Pattern(22, 2));

        var page = await new GetLearnedPatternsHandler(store.Factory, TestProjectAccess.Evaluator(AdminOfOne))
            .Handle(new GetLearnedPatternsQuery(), CancellationToken.None);

        page.Items.ToDictionary(x => x.Id, x => x.CanCurate).Should().BeEquivalentTo(new Dictionary<int, bool> { [21] = true, [22] = false });
    }

    [Test]
    public async Task ThePatchList_TellsEachRowWhatTheApplyRouteDecides()
    {
        var store = new Store();
        store.Patches.Add(Patch(31, 1, SharedSource, McpDocPatchTarget.DocumentationSection));
        store.Patches.Add(Patch(32, 2, SharedSource, McpDocPatchTarget.DocumentationSection));
        store.Patches.Add(Patch(33, 1, SharedSource, McpDocPatchTarget.ColumnDescription));
        store.Patches.Add(Patch(34, 1, OwnSource, McpDocPatchTarget.TableDescription));

        var asAdminOfOne = await new GetDocumentationPatchesHandler(store.Factory, TestProjectAccess.Evaluator(AdminOfOne), TestPermissions.None)
            .Handle(new GetDocumentationPatchesQuery(), CancellationToken.None);
        var asDataSourceAdmin = await new GetDocumentationPatchesHandler(store.Factory, TestProjectAccess.Evaluator(EditorOfBoth), TestPermissions.Holding(BeaconPermissions.DataSourceAdmin))
            .Handle(new GetDocumentationPatchesQuery(), CancellationToken.None);

        asAdminOfOne.Items.ToDictionary(x => x.Id, x => x.CanCurate).Should().BeEquivalentTo(
            new Dictionary<int, bool> { [31] = true, [32] = false, [33] = false, [34] = true });
        asDataSourceAdmin.Items.ToDictionary(x => x.Id, x => x.CanCurate).Should().BeEquivalentTo(
            new Dictionary<int, bool> { [31] = false, [32] = false, [33] = true, [34] = true });
    }

    [Test]
    public async Task ThePatternAndPatchLists_NameWhoReviewedOrDecided_ByDisplayNameOrId()
    {
        var store = new Store();
        var reviewedAt = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        var reviewed = Pattern(21, 1);
        reviewed.Status = McpPatternStatus.Approved;
        reviewed.ReviewedByUserId = 5;
        reviewed.ReviewedAt = reviewedAt;
        store.Patterns.Add(reviewed);
        store.Patterns.Add(Pattern(22, 1));
        var applied = Patch(31, 1, SharedSource, McpDocPatchTarget.DocumentationSection);
        applied.Status = McpDocPatchStatus.Applied;
        applied.AppliedByUserId = 6;
        store.Patches.Add(applied);
        store.Patches.Add(Patch(32, 1, SharedSource, McpDocPatchTarget.DocumentationSection));
        store.Users.Add(new BeaconUser { Id = 5, ExternalId = "ext-5", UserName = "ada", DisplayName = "Ada Lovelace" });
        store.Users.Add(new BeaconUser { Id = 6, ExternalId = "ext-6", UserName = "ben" });

        var patterns = await new GetLearnedPatternsHandler(store.Factory, TestProjectAccess.Evaluator(AdminOfOne))
            .Handle(new GetLearnedPatternsQuery(), CancellationToken.None);
        var patches = await new GetDocumentationPatchesHandler(store.Factory, TestProjectAccess.Evaluator(AdminOfOne), TestPermissions.None)
            .Handle(new GetDocumentationPatchesQuery(), CancellationToken.None);

        patterns.Items.Should().BeEquivalentTo(
            new[]
            {
                new { Id = 21, ReviewedByName = (string?)"Ada Lovelace", ReviewedAt = (DateTime?)reviewedAt },
                new { Id = 22, ReviewedByName = (string?)null, ReviewedAt = (DateTime?)null }
            },
            x => x.ExcludingMissingMembers());
        patches.Items.Should().BeEquivalentTo(
            new[]
            {
                new { Id = 31, AppliedByName = (string?)"User 6" },
                new { Id = 32, AppliedByName = (string?)null }
            },
            x => x.ExcludingMissingMembers(),
            "a user without a display name is named by id, never by user name");
    }

    [Test]
    public async Task TheRelationshipList_TellsWhetherTheCallerMayVerify()
    {
        var store = new Store();
        store.Relationships.Add(Relationship(SharedSource, isVerified: false));

        var adminOfBoth = await RelationshipsHandler(store, AdminOfBoth).Handle(new GetSchemaRelationshipsQuery { DataSourceId = SharedSource }, CancellationToken.None);
        var adminOfOne = await RelationshipsHandler(store, AdminOfOne).Handle(new GetSchemaRelationshipsQuery { DataSourceId = SharedSource }, CancellationToken.None);

        adminOfBoth.Items.Should().ContainSingle().Which.CanCurate.Should().BeTrue();
        adminOfOne.Items.Should().ContainSingle().Which.CanCurate.Should().BeFalse();
    }

    // A DataSourceAdmin curates a source's shared grounding only where it may write the source: a Viewer holding the
    // permission is offered nothing to curate.
    [Test]
    public async Task TheRelationshipList_OffersAViewerHoldingDataSourceAdmin_NoCuration()
    {
        var store = new Store();
        store.Relationships.Add(Relationship(SharedSource, isVerified: false));

        var page = await new GetSchemaRelationshipsHandler(store.Factory, TestProjectAccess.Evaluator(Viewer), TestPermissions.Holding(BeaconPermissions.DataSourceAdmin))
            .Handle(new GetSchemaRelationshipsQuery { DataSourceId = SharedSource }, CancellationToken.None);

        page.Items.Should().ContainSingle().Which.CanCurate.Should().BeFalse();
    }

    [Test]
    public async Task TheDocumentation_TellsWhetherTheCallerMayEditItsSections()
    {
        var store = new Store();

        var own = await new GetProjectDocumentationHandler(store.Factory, TestProjectAccess.Evaluator(AdminOfOne))
            .Handle(new GetProjectDocumentationQuery(1), CancellationToken.None);
        var other = await new GetProjectDocumentationHandler(store.Factory, TestProjectAccess.Evaluator(AdminOfOne))
            .Handle(new GetProjectDocumentationQuery(2), CancellationToken.None);

        own.CanCurate.Should().BeTrue();
        other.CanCurate.Should().BeFalse();
    }

    [Test]
    public async Task ARelationshipCreatedBySomeoneWhoMayNotCurate_IsStoredUnverified()
    {
        var byEditor = new Store();
        var byCurator = new Store();
        var byDataSourceAdmin = new Store();

        var editor = await CreateHandler(byEditor, EditorOfBoth, TestPermissions.None).Handle(NewRelationship(), CancellationToken.None);
        var curator = await CreateHandler(byCurator, AdminOfBoth, TestPermissions.None).Handle(NewRelationship(), CancellationToken.None);
        var dataSourceAdmin = await CreateHandler(byDataSourceAdmin, EditorOfBoth, TestPermissions.Holding(BeaconPermissions.DataSourceAdmin)).Handle(NewRelationship(), CancellationToken.None);

        editor.IsVerified.Should().BeFalse();
        byEditor.Saved.OfType<SchemaRelationship>().Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { IsVerified = false, VerifiedByUserId = (int?)null, VerifiedTime = (DateTime?)null },
            x => x.ExcludingMissingMembers());
        curator.IsVerified.Should().BeTrue();
        byCurator.Saved.OfType<SchemaRelationship>().Should().ContainSingle().Which.VerifiedByUserId.Should().Be(42);
        dataSourceAdmin.IsVerified.Should().BeTrue();
    }

    [Test]
    public async Task ARelationshipAnAdminCreates_IsStoredVerified_ByTheAdmin()
    {
        var store = new Store();
        var before = DateTime.UtcNow;

        var result = await CreateHandler(store, TestProjectAccess.Unrestricted, TestPermissions.None).Handle(NewRelationship(), CancellationToken.None);

        result.IsVerified.Should().BeTrue();
        var created = store.Saved.OfType<SchemaRelationship>().Should().ContainSingle().Subject;
        created.Should().BeEquivalentTo(new { IsVerified = true, VerifiedByUserId = (int?)42 }, x => x.ExcludingMissingMembers());
        created.VerifiedTime.Should().BeOnOrAfter(before);
    }

    // Either change on its own — the label, or the cardinality — is a change to a verified join: a non-curator's stores it
    // unverified, without a verifier.
    [TestCase("buyer", SchemaRelationshipCardinality.OneToMany)]
    [TestCase("customer", SchemaRelationshipCardinality.OneToOne)]
    public async Task ALabelOnlyOrCardinalityOnlyEdit_ByANonCurator_UnverifiesTheRelationship(string label, SchemaRelationshipCardinality cardinality)
    {
        var store = new Store();
        store.Relationships.Add(Relationship(SharedSource, isVerified: true));
        var edit = Relabel(label) with { Cardinality = cardinality };

        var result = await UpdateHandler(store, EditorOfBoth).Handle(edit, CancellationToken.None);

        result.IsVerified.Should().BeFalse();
        store.Relationships.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Label = label, Cardinality = cardinality, IsVerified = false, VerifiedByUserId = (int?)null, VerifiedTime = (DateTime?)null },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task UnverifyingARelationship_ClearsWhoVerifiedItAndWhen()
    {
        var store = new Store();
        store.Relationships.Add(Relationship(SharedSource, isVerified: true));

        await VerifyHandler(store).Handle(new VerifySchemaRelationshipCommand(SharedSource, 41, false), CancellationToken.None);

        store.Relationships.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { IsVerified = false, VerifiedByUserId = (int?)null, VerifiedTime = (DateTime?)null },
            x => x.ExcludingMissingMembers());
    }

    // The route checked the caller's access on the route's data source: a relationship of another source is not found there.
    [TestCase(true)]
    [TestCase(false)]
    public async Task VerifyingARelationshipOfAnotherDataSource_IsNotFound_AndChangesNothing(bool isVerified)
    {
        var store = new Store();
        store.Relationships.Add(Relationship(SharedSource, isVerified: !isVerified));

        var act = () => VerifyHandler(store).Handle(new VerifySchemaRelationshipCommand(OwnSource, 41, isVerified), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>().WithMessage("Schema relationship 41 not found.");
        store.Relationships.Should().ContainSingle().Which.IsVerified.Should().Be(!isVerified);
    }

    // A curator's change keeps the relationship verified and records the curator as its verifier, now; anyone else's
    // change unverifies it; the answer says which.
    [Test]
    public async Task EditingAVerifiedRelationship_WithoutCurate_StoresItUnverified_WhileACuratorsEditKeepsIt_AndRestampsTheVerifier()
    {
        var byEditor = new Store();
        byEditor.Relationships.Add(Relationship(SharedSource, isVerified: true));
        var byCurator = new Store();
        byCurator.Relationships.Add(Relationship(SharedSource, isVerified: true));
        var unchanged = new Store();
        unchanged.Relationships.Add(Relationship(SharedSource, isVerified: true));
        var before = DateTime.UtcNow;

        var editorResult = await UpdateHandler(byEditor, EditorOfBoth).Handle(Relabel("buyer"), CancellationToken.None);
        var curatorResult = await UpdateHandler(byCurator, AdminOfBoth).Handle(Relabel("buyer"), CancellationToken.None);
        var unchangedResult = await UpdateHandler(unchanged, EditorOfBoth).Handle(Relabel("customer"), CancellationToken.None);

        editorResult.IsVerified.Should().BeFalse();
        byEditor.Relationships.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Label = "buyer", IsVerified = false, VerifiedByUserId = (int?)null, VerifiedTime = (DateTime?)null },
            x => x.ExcludingMissingMembers());
        curatorResult.IsVerified.Should().BeTrue();
        var curated = byCurator.Relationships.Should().ContainSingle().Subject;
        curated.Should().BeEquivalentTo(
            new { Label = "buyer", IsVerified = true, VerifiedByUserId = (int?)CuratorUserId },
            x => x.ExcludingMissingMembers());
        curated.VerifiedTime.Should().BeOnOrAfter(before, "the curator verified the changed relationship now");
        unchangedResult.IsVerified.Should().BeTrue();
        unchanged.Relationships.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { IsVerified = true, VerifiedByUserId = (int?)7 },
            x => x.ExcludingMissingMembers(),
            "an edit that changes nothing changes no verification");
    }

    // The route rule is the row rule (LinkingProjects): every source on its own. A source no live project links is curated
    // by a DataSourceAdmin alone, even when another source of the same change is curated by the caller's project.
    [Test]
    public async Task ASharedChangeOverTwoSources_NeedsCurateForEachSourceOnItsOwn()
    {
        var adminOfOneOpenly = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [1] = ProjectRole.ProjectAdmin }, includesOpen: true, notOpen: []);
        var verify = ProjectCheck.CurateShared(ProjectResource.DataSource(OwnSource));
        var bothSources = ResolvedProjectResource.ThroughSources([OwnSource, UnlinkedSource]);

        var act = () => Authorizer(adminOfOneOpenly, bothSources).DemandAsync(verify, CancellationToken.None);
        await Authorizer(adminOfOneOpenly, ResolvedProjectResource.ThroughSources([OwnSource])).DemandAsync(verify, CancellationToken.None);
        await Authorizer(adminOfOneOpenly, bothSources, TestPermissions.Holding(BeaconPermissions.DataSourceAdmin)).DemandAsync(verify, CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconForbiddenException>()).Which.Message.Should().Contain("project admin role in every project that links it");
    }

    // A verified relationship is a join every linking project is told to trust: removing one is a curator's; an unverified
    // one stays the route's check.
    [Test]
    public async Task RemovingAVerifiedRelationship_NeedsACurator_AnUnverifiedOneDoesNot()
    {
        var verifiedByEditor = new Store();
        verifiedByEditor.Relationships.Add(Relationship(SharedSource, isVerified: true));
        var verifiedByCurator = new Store();
        verifiedByCurator.Relationships.Add(Relationship(SharedSource, isVerified: true));
        var unverifiedByEditor = new Store();
        unverifiedByEditor.Relationships.Add(Relationship(SharedSource, isVerified: false));

        var editor = () => DeleteHandler(verifiedByEditor, EditorOfBoth, TestPermissions.None).Handle(new DeleteSchemaRelationshipCommand(SharedSource, 41), CancellationToken.None);
        await DeleteHandler(verifiedByCurator, AdminOfBoth, TestPermissions.None).Handle(new DeleteSchemaRelationshipCommand(SharedSource, 41), CancellationToken.None);
        await DeleteHandler(unverifiedByEditor, EditorOfBoth, TestPermissions.None).Handle(new DeleteSchemaRelationshipCommand(SharedSource, 41), CancellationToken.None);

        (await editor.Should().ThrowAsync<BeaconForbiddenException>()).Which.Message.Should().Be(DeleteSchemaRelationshipHandler.VerifiedNeedsCuratorMessage);
        verifiedByEditor.Relationships.Should().ContainSingle().Which.ArchivedTime.Should().BeNull();
        verifiedByCurator.Relationships.Should().ContainSingle().Which.ArchivedTime.Should().NotBeNull();
        unverifiedByEditor.Relationships.Should().ContainSingle().Which.ArchivedTime.Should().NotBeNull();
    }

    [Test]
    public async Task RemovingAVerifiedRelationship_ByADataSourceAdmin_WhoWritesTheSource_IsAllowed()
    {
        var store = new Store();
        store.Relationships.Add(Relationship(SharedSource, isVerified: true));

        await DeleteHandler(store, EditorOfBoth, TestPermissions.Holding(BeaconPermissions.DataSourceAdmin))
            .Handle(new DeleteSchemaRelationshipCommand(SharedSource, 41), CancellationToken.None);

        store.Relationships.Should().ContainSingle().Which.ArchivedTime.Should().NotBeNull();
    }

    // Who verified is recorded: the caller's Beacon user; a caller without one is refused and nothing changes.
    [Test]
    public async Task VerifyingARelationship_RecordsTheCallersBeaconUser_AndRefusesACallerWithoutOne()
    {
        var store = new Store();
        store.Relationships.Add(Relationship(SharedSource, isVerified: false));
        var anonymous = new Mock<IBeaconActorAccessor>();
        anonymous
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeaconActor(null, true) { Kind = BeaconPrincipalKind.User, BeaconUserId = null });

        var withoutUser = () => new VerifySchemaRelationshipHandler(store.Factory, Mock.Of<ISchemaGraphService>(), anonymous.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<VerifySchemaRelationshipHandler>.Instance)
            .Handle(new VerifySchemaRelationshipCommand(SharedSource, 41, true), CancellationToken.None);
        (await withoutUser.Should().ThrowAsync<BeaconForbiddenException>()).Which.Message.Should().Be(VerifySchemaRelationshipHandler.UnidentifiedVerifierMessage);
        store.Relationships.Single().IsVerified.Should().BeFalse();

        await new VerifySchemaRelationshipHandler(store.Factory, Mock.Of<ISchemaGraphService>(), TestProjectAccess.Actors(beaconUserId: CuratorUserId), Microsoft.Extensions.Logging.Abstractions.NullLogger<VerifySchemaRelationshipHandler>.Instance)
            .Handle(new VerifySchemaRelationshipCommand(SharedSource, 41, true), CancellationToken.None);

        store.Relationships.Single().Should().BeEquivalentTo(new { IsVerified = true, VerifiedByUserId = (int?)CuratorUserId }, x => x.ExcludingMissingMembers());
    }

    private static VerifySchemaRelationshipHandler VerifyHandler(Store store)
    {
        return new VerifySchemaRelationshipHandler(
            store.Factory,
            Mock.Of<ISchemaGraphService>(),
            TestProjectAccess.Actors(beaconUserId: CuratorUserId),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VerifySchemaRelationshipHandler>.Instance);
    }

    private static DeleteSchemaRelationshipHandler DeleteHandler(Store store, ProjectAccessScope scope, IBeaconPermissionAccessor permissions)
    {
        return new DeleteSchemaRelationshipHandler(
            store.Factory,
            Mock.Of<ISchemaGraphService>(),
            TestProjectAccess.Evaluator(scope),
            permissions,
            TestProjectAccess.Actors(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DeleteSchemaRelationshipHandler>.Instance);
    }

    private static ProjectAccessScope Roles(params (int ProjectId, ProjectRole Role)[] roles)
    {
        return TestProjectAccess.Scope(roles.ToDictionary(x => x.ProjectId, x => x.Role), includesOpen: false, notOpen: []);
    }

    private static ProjectRequestAuthorizer Authorizer(
        ProjectAccessScope scope,
        ResolvedProjectResource resolved,
        IBeaconPermissionAccessor? permissions = null)
    {
        return TestProjectAccess.Authorizer(scope, new FixedResolver(resolved), new Store().Factory, permissions: permissions);
    }

    private static GetSchemaRelationshipsHandler RelationshipsHandler(Store store, ProjectAccessScope scope)
    {
        return new GetSchemaRelationshipsHandler(store.Factory, TestProjectAccess.Evaluator(scope), TestPermissions.None);
    }

    private static CreateSchemaRelationshipHandler CreateHandler(Store store, ProjectAccessScope scope, IBeaconPermissionAccessor permissions)
    {
        return new CreateSchemaRelationshipHandler(
            store.Factory,
            Mock.Of<ISchemaGraphService>(),
            Mock.Of<IBeaconUserContext>(x => x.UserId == "42"),
            TestProjectAccess.Evaluator(scope),
            permissions);
    }

    private static UpdateSchemaRelationshipHandler UpdateHandler(Store store, ProjectAccessScope scope)
    {
        return new UpdateSchemaRelationshipHandler(store.Factory, Mock.Of<ISchemaGraphService>(), TestProjectAccess.Evaluator(scope), TestPermissions.None, TestProjectAccess.Actors(beaconUserId: CuratorUserId));
    }

    private static UpdateSchemaRelationshipCommand Relabel(string label)
    {
        return new UpdateSchemaRelationshipCommand
        {
            DataSourceId = SharedSource,
            Id = 41,
            Label = label,
            Cardinality = SchemaRelationshipCardinality.OneToMany
        };
    }

    private static CreateSchemaRelationshipCommand NewRelationship()
    {
        return new CreateSchemaRelationshipCommand
        {
            DataSourceId = SharedSource,
            SourceSchema = "public",
            SourceTable = "orders",
            SourceColumn = "customer_id",
            TargetSchema = "public",
            TargetTable = "customers",
            TargetColumn = "id",
            Cardinality = SchemaRelationshipCardinality.OneToMany
        };
    }

    private static McpLearnedPattern Pattern(int id, int projectId)
    {
        return new McpLearnedPattern
        {
            Id = id,
            ProjectId = projectId,
            DataSourceId = SharedSource,
            SchemaName = "public",
            TableName = "orders",
            PatternType = McpPatternType.CommonQuery,
            PatternContent = "Common query pattern for public.orders",
            Status = McpPatternStatus.NeedsEvidence
        };
    }

    private static McpDocumentationPatch Patch(int id, int projectId, int dataSourceId, McpDocPatchTarget target)
    {
        return new McpDocumentationPatch
        {
            Id = id,
            ProjectId = projectId,
            DataSourceId = dataSourceId,
            TargetType = target,
            TargetIdentifier = "public.orders",
            ProposedContent = "Orders placed by customers.",
            Reasoning = "Asked about often.",
            Status = McpDocPatchStatus.Proposed
        };
    }

    private static SchemaRelationship Relationship(int dataSourceId, bool isVerified)
    {
        return new SchemaRelationship
        {
            Id = 41,
            DataSourceId = dataSourceId,
            SourceSchema = "public",
            SourceTable = "orders",
            SourceColumn = "customer_id",
            TargetSchema = "public",
            TargetTable = "customers",
            TargetColumn = "id",
            Label = "customer",
            Origin = SchemaRelationshipOrigin.Manual,
            Cardinality = SchemaRelationshipCardinality.OneToMany,
            Confidence = 1.0,
            IsVerified = isVerified,
            VerifiedByUserId = isVerified ? 7 : null,
            VerifiedTime = isVerified ? new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc) : null
        };
    }

    private sealed class FixedResolver(ResolvedProjectResource resolved) : IProjectResourceResolver
    {
        public Task<ResolvedProjectResource> ResolveAsync(BeaconContext context, ProjectResource resource, CancellationToken cancellationToken) =>
            Task.FromResult(resolved);
    }

    /// <summary>The deployment's projects, links and data sources, and whatever a test adds; added rows land in <see cref="Saved"/>.</summary>
    private sealed class Store
    {
        public Store()
        {
            var projects = new[] { 1, 2, 3 }
                .Select(x => new Project { Id = x, Name = $"project {x}" })
                .ToList();
            var links = new List<ProjectDataSource>
            {
                Link(projects[0], SharedSource),
                Link(projects[1], SharedSource),
                Link(projects[0], OwnSource)
            };
            var dataSources = new[] { SharedSource, OwnSource, UnlinkedSource }
                .Select(x => new DataSource { Id = x, Name = $"source {x}", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "enc" })
                .ToList();
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(Project)] = RecordingBeaconContext.MemorySet(projects, Saved),
                        [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(links, Saved),
                        [typeof(DataSource)] = RecordingBeaconContext.MemorySet(dataSources, Saved),
                        [typeof(McpLearnedPattern)] = RecordingBeaconContext.MemorySet(Patterns, Saved),
                        [typeof(McpDocumentationPatch)] = RecordingBeaconContext.MemorySet(Patches, Saved),
                        [typeof(SchemaRelationship)] = RecordingBeaconContext.MemorySet(Relationships, Saved),
                        [typeof(ProjectDocumentation)] = RecordingBeaconContext.MemorySet(new List<ProjectDocumentation>(), Saved),
                        [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(Users, Saved)
                    },
                    Saved));
            Factory = factory.Object;
        }

        public List<McpLearnedPattern> Patterns { get; } = [];

        public List<McpDocumentationPatch> Patches { get; } = [];

        public List<SchemaRelationship> Relationships { get; } = [];

        public List<BeaconUser> Users { get; } = [];

        public List<object> Saved { get; } = [];

        public IDbContextFactory<BeaconContext> Factory { get; }

        private static ProjectDataSource Link(Project project, int dataSourceId)
        {
            var link = new ProjectDataSource { ProjectId = project.Id, DataSourceId = dataSourceId, Project = project };
            project.DataSources.Add(link);

            return link;
        }
    }
}
