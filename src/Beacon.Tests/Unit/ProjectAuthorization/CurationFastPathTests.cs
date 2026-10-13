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
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Services.Metadata;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Curating grounding on the authorizer's fast path, with the scope the real evaluator gives. Where every project is open
/// and every data source reachable, an Editor reads and writes everywhere without a lookup (the list filters add nothing),
/// and still curates nothing: every curation command is refused (403), a project's own grounding and a data source's
/// shared grounding alike, and so is removing a verified relationship. A ProjectAdmin of every linking project curates;
/// an Admin passes without a read; an API key or an MCP caller of that ProjectAdmin curates nothing.
/// The deployment: open projects 1 and 2, data source 10 linked to both; user 7 is ProjectAdmin of both, user 8 of none.
/// </summary>
[TestFixture]
public class CurationFastPathTests
{
    private const int SharedSource = 10;
    private const int ProjectAdminUserId = 7;
    private const int EditorUserId = 8;

    private static IEnumerable<TestCaseData> CurationCommands()
    {
        yield return Named(new UpdatePatternStatusCommand { PatternId = 21, NewStatus = McpPatternStatus.Approved, ReviewedContentHash = "reviewed" });
        yield return Named(new UpdateDocumentationSectionCommand(6, "edited"));
        yield return Named(new GenerateProjectDocumentationCommand(1, EditorUserId));
        yield return Named(new ApplyDocumentationPatchCommand { PatchId = 31 }, "a description");
        yield return Named(new ApplyDocumentationPatchCommand { PatchId = 32 }, "a documentation section");
        yield return Named(new RejectDocumentationPatchCommand { PatchId = 31 }, "a description");
        yield return Named(new RejectDocumentationPatchCommand { PatchId = 32 }, "a documentation section");
        yield return Named(new VerifySchemaRelationshipCommand(SharedSource, 41, true));
    }

    [TestCaseSource(nameof(CurationCommands))]
    public async Task AnEditorOnTheFastPath_IsRefusedEveryCuration(IProjectAuthorizedRequest command)
    {
        var store = new Store();
        var scope = await TestProjectAccess.RealEvaluator(User(EditorUserId), store.Factory).GetScopeAsync(CancellationToken.None);

        var act = () => Authorizer(scope, store).DemandAsync(command.ProjectChecks, CancellationToken.None);

        scope.For(ProjectAction.Read).Should().BeSameAs(ProjectSet.All, "the deployment is all open: the fast path");
        scope.For(ProjectAction.Write).Should().BeSameAs(ProjectSet.All);
        scope.For(ProjectAction.Curate).Ids.Should().BeEmpty("an open grant never curates");
        await act.Should().ThrowAsync<BeaconForbiddenException>();
    }

    [TestCaseSource(nameof(CurationCommands))]
    public async Task AProjectAdminOfEveryLinkingProject_Curates(IProjectAuthorizedRequest command)
    {
        var store = new Store();
        var scope = await TestProjectAccess.RealEvaluator(User(ProjectAdminUserId), store.Factory).GetScopeAsync(CancellationToken.None);

        await Authorizer(scope, store).DemandAsync(command.ProjectChecks, CancellationToken.None);

        scope.For(ProjectAction.Read).Should().BeSameAs(ProjectSet.All);
        scope.For(ProjectAction.Curate).Ids.Should().Equal(1, 2);
    }

    [TestCaseSource(nameof(CurationCommands))]
    public async Task AnAdmin_CuratesWithoutARead(IProjectAuthorizedRequest command)
    {
        var store = new Store();
        var scope = await TestProjectAccess.RealEvaluator(User(ProjectAdminUserId) with { IsLiveAdmin = true }, store.Factory).GetScopeAsync(CancellationToken.None);

        await TestProjectAccess.Authorizer(scope).DemandAsync(command.ProjectChecks, CancellationToken.None);

        scope.IsUnrestricted.Should().BeTrue();
    }

    // Curating is an interactive session's: the ProjectAdmin's own API key and an MCP caller acting as the ProjectAdmin
    // keep every other action of the role, and curate nothing.
    [TestCaseSource(nameof(CurationCommands))]
    public async Task AnApiKeyOrAnMcpCallerOfAProjectAdmin_IsRefusedEveryCuration(IProjectAuthorizedRequest command)
    {
        var store = new Store();
        var key = new BeaconActor("ext-7", false)
        {
            Kind = BeaconPrincipalKind.ApiKey,
            IsApiKey = true,
            BeaconUserId = ProjectAdminUserId,
            IsActiveUser = true,
            ScopeCeiling = McpCallerScope.Execute
        };
        var mcpUser = new BeaconActor("ext-7", false)
        {
            Kind = BeaconPrincipalKind.McpCaller,
            BeaconUserId = ProjectAdminUserId,
            IsActiveUser = true,
            AllowedProjectIds = [1, 2],
            ScopeCeiling = McpCallerScope.Execute,
            CallerKind = McpCallerKind.User
        };

        foreach (var caller in new[] { key, mcpUser })
        {
            var scope = await TestProjectAccess.RealEvaluator(caller, store.Factory).GetScopeAsync(caller, CancellationToken.None);

            var act = () => Authorizer(scope, store).DemandAsync(command.ProjectChecks, CancellationToken.None);

            scope.Allows(1, ProjectAction.Manage).Should().BeTrue($"the {caller.Kind} acts as its ProjectAdmin otherwise");
            await act.Should().ThrowAsync<BeaconForbiddenException>($"a {caller.Kind} never curates");
        }
    }

    // Removing a verified relationship is checked by its handler with the same rule: the route's check passes an Editor on
    // the fast path (it manages the source), the handler refuses it and archives nothing; a ProjectAdmin of every linking
    // project removes it.
    [Test]
    public async Task RemovingAVerifiedRelationship_OnTheFastPath_NeedsACurator()
    {
        var byEditor = new Store();
        var byProjectAdmin = new Store();
        var command = new DeleteSchemaRelationshipCommand(SharedSource, 41);

        var editorScope = await TestProjectAccess.RealEvaluator(User(EditorUserId), byEditor.Factory).GetScopeAsync(CancellationToken.None);
        await Authorizer(editorScope, byEditor).DemandAsync(((IProjectAuthorizedRequest)command).ProjectChecks, CancellationToken.None);
        var editor = () => DeleteHandler(byEditor, User(EditorUserId)).Handle(command, CancellationToken.None);
        await DeleteHandler(byProjectAdmin, User(ProjectAdminUserId)).Handle(command, CancellationToken.None);

        (await editor.Should().ThrowAsync<BeaconForbiddenException>()).Which.Message.Should().Be(DeleteSchemaRelationshipHandler.VerifiedNeedsCuratorMessage);
        byEditor.Relationships.Should().ContainSingle().Which.ArchivedTime.Should().BeNull();
        byProjectAdmin.Relationships.Should().ContainSingle().Which.ArchivedTime.Should().NotBeNull();
    }

    private static TestCaseData Named(IProjectAuthorizedRequest command, string? target = null)
    {
        var name = command.GetType().Name;

        return new TestCaseData(command).SetArgDisplayNames(target == null ? name : $"{name} of {target}");
    }

    private static BeaconActor User(int beaconUserId)
    {
        return new BeaconActor($"ext-{beaconUserId}", false)
        {
            Kind = BeaconPrincipalKind.User,
            BeaconUserId = beaconUserId,
            IsActiveUser = true,
            RoleLevel = 2
        };
    }

    private static ProjectRequestAuthorizer Authorizer(ProjectAccessScope scope, Store store)
    {
        return TestProjectAccess.Authorizer(scope, new ProjectResourceResolver(), store.Factory);
    }

    private static DeleteSchemaRelationshipHandler DeleteHandler(Store store, BeaconActor caller)
    {
        return new DeleteSchemaRelationshipHandler(
            store.Factory,
            Mock.Of<ISchemaGraphService>(),
            TestProjectAccess.RealEvaluator(caller, store.Factory),
            TestPermissions.None,
            TestProjectAccess.Actors(),
            NullLogger<DeleteSchemaRelationshipHandler>.Instance);
    }

    /// <summary>The all-open deployment and the grounding the curation commands name, served by every context.</summary>
    private sealed class Store
    {
        public Store()
        {
            var projects = new[] { 1, 2 }
                .Select(x => new Project { Id = x, Name = $"project {x}" })
                .ToList();
            var source = new DataSource { Id = SharedSource, Name = "sales", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "enc" };
            var links = projects
                .Select(x => new ProjectDataSource { ProjectId = x.Id, DataSourceId = SharedSource, Project = x, DataSource = source })
                .ToList();
            var members = projects
                .Select(x => new ProjectMember { ProjectId = x.Id, UserId = ProjectAdminUserId, Role = ProjectRole.ProjectAdmin })
                .ToList();
            var documentation = new ProjectDocumentation { Id = 5, ProjectId = 1, GeneratedByModel = "model" };
            var sections = new List<ProjectDocumentationSection>
            {
                new() { Id = 6, ProjectDocumentationId = 5, Title = "Overview", Content = "c", Documentation = documentation }
            };
            var patterns = new List<McpLearnedPattern>
            {
                new() { Id = 21, ProjectId = 1, DataSourceId = SharedSource, SchemaName = "public", TableName = "orders", PatternContent = "c", Status = McpPatternStatus.NeedsEvidence }
            };
            var patches = new List<McpDocumentationPatch>
            {
                Patch(31, McpDocPatchTarget.ColumnDescription),
                Patch(32, McpDocPatchTarget.DocumentationSection)
            };
            Relationships.Add(new SchemaRelationship
            {
                Id = 41,
                DataSourceId = SharedSource,
                SourceSchema = "public",
                SourceTable = "orders",
                SourceColumn = "customer_id",
                TargetSchema = "public",
                TargetTable = "customers",
                TargetColumn = "id",
                Label = "customer",
                Origin = SchemaRelationshipOrigin.Manual,
                IsVerified = true,
                VerifiedByUserId = ProjectAdminUserId
            });
            var saved = new List<object>();
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(Project)] = RecordingBeaconContext.MemorySet(projects, saved),
                        [typeof(DataSource)] = RecordingBeaconContext.MemorySet([source], saved),
                        [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(links, saved),
                        [typeof(ProjectMember)] = RecordingBeaconContext.MemorySet(members, saved),
                        [typeof(ProjectDocumentationSection)] = RecordingBeaconContext.MemorySet(sections, saved),
                        [typeof(McpLearnedPattern)] = RecordingBeaconContext.MemorySet(patterns, saved),
                        [typeof(McpDocumentationPatch)] = RecordingBeaconContext.MemorySet(patches, saved),
                        [typeof(SchemaRelationship)] = RecordingBeaconContext.MemorySet(Relationships, saved)
                    },
                    saved));
            Factory = factory.Object;
        }

        public List<SchemaRelationship> Relationships { get; } = [];

        public IDbContextFactory<BeaconContext> Factory { get; }

        private static McpDocumentationPatch Patch(int id, McpDocPatchTarget target)
        {
            return new McpDocumentationPatch
            {
                Id = id,
                ProjectId = 1,
                DataSourceId = SharedSource,
                TargetType = target,
                TargetIdentifier = "public.orders",
                ProposedContent = "Orders placed by customers.",
                Reasoning = "Asked about often.",
                Status = McpDocPatchStatus.Proposed
            };
        }
    }
}
