using FluentAssertions;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Models.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpGlossary;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Tier-3 ⑪ coverage for the business-glossary injection in
/// <see cref="KnowledgeGraphService.BuildGlossaryBlockAsync"/> plus the CRUD handlers. Exercises the real
/// injection logic against a mocked <see cref="BeaconContext"/> backed by the async-queryable doubles (no
/// DB, no forbidden <c>UseInMemoryDatabase</c> — §4.7). The PostgreSQL raw <c>&lt;=&gt;</c> path needs a
/// live pgvector DB, so the context spoofs a non-Npgsql provider and the deterministic in-memory cosine
/// path runs with <see cref="FakeEmbeddingService"/>.
/// </summary>
[TestFixture]
public class GlossaryInjectionTests
{
    private const int DataSourceId = 7;
    private const int ProjectId = 3;
    private const string Question = "What is our total revenue this year?";

    [Test]
    public async Task BuildGlossaryBlock_InjectsTopKActiveTermsOrderedBySimilarity_InactiveFiltered()
    {
        var embedder = new FakeEmbeddingService();
        var queryVector = await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(Question), CancellationToken.None);

        // Four terms. #4 is INACTIVE and stores the query-identical vector (cosine 1.0 → the single nearest
        // hit), so if the IsActive filter were missing it would lead the block. The three active terms carry
        // distinct definitions/targets so their rendering is unambiguous.
        var vectorById = new Dictionary<int, float[]>
        {
            [1] = await embedder.EmbedAsync("gross sales money earned", CancellationToken.None),
            [2] = await embedder.EmbedAsync("people signing in recently", CancellationToken.None),
            [3] = await embedder.EmbedAsync("customers leaving cancellations", CancellationToken.None),
            [4] = queryVector
        };

        const int topK = 5; // ≥ term count so every embedding is a hit and the IsActive filter is the only gate.

        // Independently compute the NN order the in-memory path must produce, then drop the inactive #4 —
        // that is the exact term order the rendered block must follow.
        var expectedActiveOrder = vectorById
            .Select(x => (Id: x.Key, Score: EmbeddingCodec.Cosine(queryVector, x.Value)))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Id)
            .Where(id => id != 4)
            .ToList();

        var terms = new List<McpGlossaryTerm>
        {
            NewTerm(1, "revenue", "Total sales amount recognized", isActive: true, metric: "SUM(amount)"),
            NewTerm(2, "active user", "A user with a login in the last 30 days", isActive: true,
                schema: "analytics", table: "users", column: "last_login"),
            NewTerm(3, "churn", "Customers who canceled their subscription", isActive: true),
            NewTerm(4, "top secret", "SHOULD NOT APPEAR IN THE BLOCK", isActive: false)
        };

        var embeddings = vectorById
            .Select(x => NewGlossaryEmbedding(x.Key, x.Value))
            .ToList();

        var service = BuildService(embedder, terms, embeddings, semanticEnabled: true, glossaryTopK: topK);

        var block = await service.BuildGlossaryBlockAsync(
            ProjectId, Question, new McpSettingsData { EnableSemanticRetrieval = true, GlossaryTopK = topK },
            CancellationToken.None);

        block.Should().Contain("## Business Glossary");

        // The inactive term is the nearest hit but MUST be filtered out (its embedding maps to no active row).
        block.Should().NotContain("SHOULD NOT APPEAR IN THE BLOCK");
        block.Should().NotContain("top secret");

        // All three active definitions are injected, with their target column / metric rendered.
        block.Should().Contain("Total sales amount recognized");
        block.Should().Contain("`SUM(amount)`");
        block.Should().Contain("A user with a login in the last 30 days");
        block.Should().Contain("analytics.users.last_login");
        block.Should().Contain("Customers who canceled their subscription");

        // Rendered term order == the independently-computed similarity order (active-only).
        var renderedOrder = RenderedTermIds(block);
        renderedOrder.Should().Equal(expectedActiveOrder,
            "the block renders the nearest active glossary terms in descending-similarity order");
    }

    // A metric expression is SQL the model copies into its answer: rendered without its comments in the asked data source's
    // dialect; one that cannot be shown so is left out (the term keeps its definition), logged by term id once per interval.
    [Test]
    public async Task BuildGlossaryBlock_RendersMetricExpressionsWithoutComments_AndLeavesOutWhatCannotBeShown()
    {
        var embedder = new FakeEmbeddingService();
        var terms = new List<McpGlossaryTerm>
        {
            NewTerm(1, "revenue", "Total sales amount recognized", isActive: true, metric: "SUM(amount) -- metric-note"),
            NewTerm(2, "margin", "Revenue less cost", isActive: true, metric: "SUM(amount - cost) /* never closed"),
            NewTerm(3, "refunds", "Refunded orders", isActive: true, metric: "COUNT(*) FILTER (WHERE note = 'a--b')")
        };
        var embeddings = new List<McpEmbedding>
        {
            NewGlossaryEmbedding(1, await embedder.EmbedAsync("gross sales money earned", CancellationToken.None)),
            NewGlossaryEmbedding(2, await embedder.EmbedAsync("profit after costs", CancellationToken.None)),
            NewGlossaryEmbedding(3, await embedder.EmbedAsync("orders returned", CancellationToken.None))
        };
        var logs = new LogRecorder();
        var service = BuildService(embedder, terms, embeddings, semanticEnabled: true, glossaryTopK: 5, logs);
        var settings = new McpSettingsData { EnableSemanticRetrieval = true, GlossaryTopK = 5 };

        var block = await service.BuildGlossaryBlockAsync(ProjectId, Question, settings, "PostgreSQL", CancellationToken.None);
        await service.BuildGlossaryBlockAsync(ProjectId, Question, settings, "PostgreSQL", CancellationToken.None);

        block.Should().Contain("- **revenue**: Total sales amount recognized (→ `SUM(amount)`)");
        block.Should().Contain("- **margin**: Revenue less cost").And.NotContain("never closed").And.NotContain("metric-note");
        block.Should().Contain("`COUNT(*) FILTER (WHERE note = 'a--b')`");
        logs.Entries.Should().ContainSingle(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Warning, "logged once per interval")
            .Which.Message.Should().EndWith("(terms 2)");
    }

    // Without the asked data source's dialect, an expression holding a comment marker cannot be read safely: left out.
    [Test]
    public async Task BuildGlossaryBlock_WithoutADialect_LeavesOutAMetricExpressionHoldingACommentMarker()
    {
        var embedder = new FakeEmbeddingService();
        var terms = new List<McpGlossaryTerm> { NewTerm(1, "revenue", "Total sales amount recognized", isActive: true, metric: "SUM(amount) -- metric-note") };
        var embeddings = new List<McpEmbedding> { NewGlossaryEmbedding(1, await embedder.EmbedAsync("gross sales money earned", CancellationToken.None)) };
        var service = BuildService(embedder, terms, embeddings, semanticEnabled: true, glossaryTopK: 5);

        var block = await service.BuildGlossaryBlockAsync(ProjectId, Question, new McpSettingsData { EnableSemanticRetrieval = true, GlossaryTopK = 5 }, CancellationToken.None);

        block.Should().Contain("- **revenue**: Total sales amount recognized").And.NotContain("metric-note").And.NotContain("SUM(amount)");
    }

    [Test]
    public async Task BuildGlossaryBlock_TwoProjectsSharingADataSource_ReturnsOnlyTheCallersProjectsTerms()
    {
        // The leak this guards against (codex PR-11 R4): the block builder used to DERIVE the project
        // from the data source's first (lowest-Id) ProjectDataSources link — a data source shared by two
        // projects served the lower project's terms to the other project's callers. The project is now
        // the caller's authorized projectId, passed explicitly.
        var embedder = new FakeEmbeddingService();
        var queryVector = await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(Question), CancellationToken.None);

        const int otherProjectId = 1; // LOWER than ProjectId — the old lowest-link derivation would pick it
        var terms = new List<McpGlossaryTerm>
        {
            NewTerm(1, "revenue", "Total sales amount recognized", isActive: true),
            new()
            {
                Id = 2,
                ProjectId = otherProjectId,
                Term = "secret metric",
                Definition = "OTHER PROJECT'S DEFINITION",
                IsActive = true
            }
        };

        // BOTH terms store the query-identical vector — each is a perfect hit absent project scoping.
        var embeddings = new List<McpEmbedding>
        {
            NewGlossaryEmbedding(1, queryVector),
            new()
            {
                Id = 600,
                DataSourceId = 0,
                ProjectId = otherProjectId,
                OwnerType = McpEmbeddingOwnerType.GlossaryTerm,
                OwnerId = 2,
                EmbeddingBytes = EmbeddingCodec.ToBytes(queryVector),
                Model = "bge-small-en-v1.5",
                Dimensions = 384,
                EmbeddingVersion = 1
            }
        };

        var service = BuildService(embedder, terms, embeddings, semanticEnabled: true, glossaryTopK: 5);

        var block = await service.BuildGlossaryBlockAsync(
            ProjectId, Question, new McpSettingsData { EnableSemanticRetrieval = true, GlossaryTopK = 5 },
            CancellationToken.None);

        block.Should().Contain("Total sales amount recognized",
            "the caller's own project's terms must be injected");
        block.Should().NotContain("secret metric");
        block.Should().NotContain("OTHER PROJECT'S DEFINITION",
            "another project's glossary must NEVER leak through a shared data source");
    }

    [Test]
    public async Task BuildGlossaryBlock_WhenEmbedderUnavailable_InjectsNothing()
    {
        // Behaviour-preserving fallback: no embedder → no glossary block at all, even with active terms + vectors.
        var terms = new List<McpGlossaryTerm>
        {
            NewTerm(1, "revenue", "Total sales amount recognized", isActive: true, metric: "SUM(amount)")
        };
        var embeddings = new List<McpEmbedding> { NewGlossaryEmbedding(1, new float[384]) };

        var service = BuildService(
            new UnavailableEmbeddingService(), terms, embeddings, semanticEnabled: true, glossaryTopK: 5);

        var block = await service.BuildGlossaryBlockAsync(
            ProjectId, Question, new McpSettingsData { EnableSemanticRetrieval = true, GlossaryTopK = 5 },
            CancellationToken.None);

        block.Should().BeEmpty();
    }

    [Test]
    public async Task BuildGlossaryBlock_WhenSemanticRetrievalDisabled_InjectsNothing()
    {
        var embedder = new FakeEmbeddingService();
        var queryVector = await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(Question), CancellationToken.None);

        var terms = new List<McpGlossaryTerm>
        {
            NewTerm(1, "revenue", "Total sales amount recognized", isActive: true)
        };
        var embeddings = new List<McpEmbedding> { NewGlossaryEmbedding(1, queryVector) };

        var service = BuildService(embedder, terms, embeddings, semanticEnabled: false, glossaryTopK: 5);

        var block = await service.BuildGlossaryBlockAsync(
            ProjectId, Question, new McpSettingsData { EnableSemanticRetrieval = false, GlossaryTopK = 5 },
            CancellationToken.None);

        block.Should().BeEmpty();
    }

    [Test]
    public async Task CreateGlossaryTermHandler_AddsTermWithSuppliedFields()
    {
        McpGlossaryTerm? added = null;
        var set = BuildDbSet(new List<McpGlossaryTerm>());
        set.Setup(x => x.Add(It.IsAny<McpGlossaryTerm>()))
            .Callback<McpGlossaryTerm>(x => added = x);

        var context = new GlossaryHandlerContext(set.Object);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var handler = new CreateGlossaryTermHandler(factory.Object, new RecordingSecurityAudit());

        await handler.Handle(new CreateGlossaryTermCommand
        {
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            Term = "revenue",
            Synonyms = "sales, turnover",
            Definition = "Total sales amount recognized",
            MetricExpression = "SUM(amount)"
        }, CancellationToken.None);

        added.Should().NotBeNull();
        added!.ProjectId.Should().Be(ProjectId);
        added.DataSourceId.Should().Be(DataSourceId);
        added.Term.Should().Be("revenue");
        added.Synonyms.Should().Be("sales, turnover");
        added.Definition.Should().Be("Total sales amount recognized");
        added.MetricExpression.Should().Be("SUM(amount)");
        added.IsActive.Should().BeTrue("new terms are active on creation");
    }

    [Test]
    public async Task CreateGlossaryTermHandler_ADataSourceNotLinkedToTheProject_IsRefused()
    {
        var set = BuildDbSet(new List<McpGlossaryTerm>());
        var context = new GlossaryHandlerContext(set.Object, links: []);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);
        var handler = new CreateGlossaryTermHandler(factory.Object, new RecordingSecurityAudit());

        var act = () => handler.Handle(new CreateGlossaryTermCommand
        {
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            Term = "revenue",
            Definition = "Total sales amount recognized"
        }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not linked to project*");
        set.Verify(x => x.Add(It.IsAny<McpGlossaryTerm>()), Times.Never);
    }

    [Test]
    public async Task CreateGlossaryTermHandler_BlankTerm_Throws()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict);
        var handler = new CreateGlossaryTermHandler(factory.Object, new RecordingSecurityAudit());

        var act = async () => await handler.Handle(new CreateGlossaryTermCommand
        {
            ProjectId = ProjectId,
            Term = "   ",
            Definition = "something"
        }, CancellationToken.None);

        // The guard runs before any DB access, so the strict factory is never touched.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*term text is required*");
    }

    [Test]
    public async Task UpdateGlossaryTermHandler_WhenMissing_Throws()
    {
        var context = new GlossaryHandlerContext(BuildDbSet(new List<McpGlossaryTerm>()).Object);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var handler = new UpdateGlossaryTermHandler(factory.Object, new RecordingSecurityAudit());

        var act = async () => await handler.Handle(new UpdateGlossaryTermCommand { Id = 404, Term = "x" },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*404 not found*");
    }

    [Test]
    public async Task CreatingEditingAndDeletingATerm_AreRecorded_ByIdFieldNamesAndContentDigests_NeverTheirText()
    {
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, "ext-ada"));
        var set = BuildDbSet(new List<McpGlossaryTerm>());
        var created = new Mock<IDbContextFactory<BeaconContext>>();
        created
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GlossaryHandlerContext(set.Object));
        var term = NewTerm(1, "revenue", "Total sales amount recognized", isActive: true);

        await new CreateGlossaryTermHandler(created.Object, audit).Handle(
            new CreateGlossaryTermCommand { ProjectId = ProjectId, DataSourceId = DataSourceId, Term = "net revenue", Definition = "Sales minus refunds" },
            CancellationToken.None);
        await new UpdateGlossaryTermHandler(BuildGlossaryHandlerFactory(term), audit).Handle(
            new UpdateGlossaryTermCommand { Id = 1, Definition = "Sales recognized this period", IsActive = false },
            CancellationToken.None);
        await new DeleteGlossaryTermHandler(BuildGlossaryHandlerFactory(term), audit).Handle(new DeleteGlossaryTermCommand(1), CancellationToken.None);

        audit.Events.Select(x => x.Action).Should().Equal(
            SecurityAuditActions.GlossaryTermCreated,
            SecurityAuditActions.GlossaryTermUpdated,
            SecurityAuditActions.GlossaryTermDeleted);
        audit.Ways.Should().Equal("saved", "staged", "staged");
        audit.Events.Should().OnlyContain(x => x.ProjectId == ProjectId && x.ResourceType == SecurityAuditResources.GlossaryTerm);
        const string contentFields = "\"contentFields\":[\"term\",\"synonyms\",\"definition\",\"targetSchema\",\"targetTable\",\"targetColumn\",\"metricExpression\"]";
        audit.Of(SecurityAuditActions.GlossaryTermCreated).Single().DetailsJson.Should().Be(
            $"{{\"dataSourceId\":{DataSourceId},{contentFields},\"afterDigests\":{Digests("net revenue", null, "Sales minus refunds", null, null, null, null)}}}");
        audit.Of(SecurityAuditActions.GlossaryTermUpdated).Single().DetailsJson.Should().Be(
            $"{{\"fields\":[\"definition\",\"isActive\"],\"dataSourceId\":{(term.DataSourceId?.ToString() ?? "null")},\"isActive\":false,{contentFields},"
            + $"\"beforeDigests\":{Digests("revenue", term.Synonyms, "Total sales amount recognized", term.TargetSchema, term.TargetTable, term.TargetColumn, term.MetricExpression)},"
            + $"\"afterDigests\":{Digests("revenue", term.Synonyms, "Sales recognized this period", term.TargetSchema, term.TargetTable, term.TargetColumn, term.MetricExpression)}}}");
        audit.Of(SecurityAuditActions.GlossaryTermDeleted).Single().DetailsJson.Should().Be(
            $"{{{contentFields},\"beforeDigests\":{Digests("revenue", term.Synonyms, "Sales recognized this period", term.TargetSchema, term.TargetTable, term.TargetColumn, term.MetricExpression)}}}");
        foreach (var auditEvent in audit.Events)
        {
            SecurityAuditTestKit.AssertNoPii(auditEvent, "revenue", "Sales minus refunds", "Sales recognized", "Total sales amount");
        }
    }

    [Test]
    public async Task DeleteGlossaryTermHandler_SoftDeactivates_TermRetained()
    {
        var term = NewTerm(1, "revenue", "Total sales amount recognized", isActive: true);
        var handler = new DeleteGlossaryTermHandler(BuildGlossaryHandlerFactory(term), new RecordingSecurityAudit());

        await handler.Handle(new DeleteGlossaryTermCommand(1), CancellationToken.None);

        // Soft delete: the flag flips to false but the row itself is retained (governed history).
        term.IsActive.Should().BeFalse();
    }

    [Test]
    public async Task DeleteGlossaryTermHandler_WhenMissing_Throws()
    {
        var handler = new DeleteGlossaryTermHandler(BuildGlossaryHandlerFactory(), new RecordingSecurityAudit());

        var act = async () => await handler.Handle(new DeleteGlossaryTermCommand(404), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*404 not found*");
    }

    [Test]
    public async Task GetGlossaryTermsHandler_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        var handler = new GetGlossaryTermsHandler(BuildGlossaryHandlerFactory(
            NewTerm(1, "revenue", "def", isActive: true),
            NewTerm(2, "legacy", "def", isActive: false)));

        var activeOnly = await handler.Handle(
            new GetGlossaryTermsQuery { ProjectId = ProjectId }, CancellationToken.None);
        activeOnly.Select(x => x.Id).Should().Equal(1);

        var all = await handler.Handle(
            new GetGlossaryTermsQuery { ProjectId = ProjectId, IncludeInactive = true }, CancellationToken.None);
        all.Select(x => x.Id).Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Test]
    public async Task GetGlossaryTermsHandler_ScopesToDataSource_WhenSupplied()
    {
        var handler = new GetGlossaryTermsHandler(BuildGlossaryHandlerFactory(
            NewTermWithDataSource(1, "revenue", dataSourceId: DataSourceId),
            NewTermWithDataSource(2, "other", dataSourceId: 999)));

        var scoped = await handler.Handle(
            new GetGlossaryTermsQuery { ProjectId = ProjectId, DataSourceId = DataSourceId }, CancellationToken.None);

        scoped.Select(x => x.Id).Should().Equal(1);
    }

    [Test]
    public async Task UpdateGlossaryTermHandler_AppliesSuppliedFields_LeavesOthersUnchanged()
    {
        var term = NewTerm(1, "revenue", "Old definition", isActive: true, metric: "SUM(amount)");
        var handler = new UpdateGlossaryTermHandler(BuildGlossaryHandlerFactory(term), new RecordingSecurityAudit());

        await handler.Handle(new UpdateGlossaryTermCommand { Id = 1, Definition = "New definition" }, CancellationToken.None);

        term.Definition.Should().Be("New definition", "the supplied field is applied");
        term.Term.Should().Be("revenue", "unsupplied fields are left unchanged");
        term.MetricExpression.Should().Be("SUM(amount)", "unsupplied fields are left unchanged");
    }

    [Test]
    public async Task UpdateGlossaryTermHandler_BlankTerm_Throws_AndLeavesTermUnchanged()
    {
        var term = NewTerm(1, "revenue", "def", isActive: true);
        var handler = new UpdateGlossaryTermHandler(BuildGlossaryHandlerFactory(term), new RecordingSecurityAudit());

        var act = async () => await handler.Handle(new UpdateGlossaryTermCommand { Id = 1, Term = "   " }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*term text cannot be blank*");
        term.Term.Should().Be("revenue", "the guard must not blank the stored term");
    }

    [Test]
    public async Task UpdateGlossaryTermHandler_BlankDefinition_Throws()
    {
        var term = NewTerm(1, "revenue", "def", isActive: true);
        var handler = new UpdateGlossaryTermHandler(BuildGlossaryHandlerFactory(term), new RecordingSecurityAudit());

        var act = async () => await handler.Handle(new UpdateGlossaryTermCommand { Id = 1, Definition = " " }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*definition cannot be blank*");
    }

    // The keyed digests an event holds of a term's text fields, as JSON, in the order of the event's contentFields.
    private static string Digests(params string?[] values)
    {
        return "[" + string.Join(",", values.Select(x => $"\"{SecurityAuditTestKit.Integrity.Digest(x)}\"")) + "]";
    }

    private static IDbContextFactory<BeaconContext> BuildGlossaryHandlerFactory(params McpGlossaryTerm[] terms)
    {
        var context = new GlossaryHandlerContext(BuildDbSet(terms.ToList()).Object);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);
        return factory.Object;
    }

    private static McpGlossaryTerm NewTermWithDataSource(int id, string term, int dataSourceId) =>
        new()
        {
            Id = id,
            ProjectId = ProjectId,
            DataSourceId = dataSourceId,
            Term = term,
            Definition = "def",
            IsActive = true
        };

    private static List<int> RenderedTermIds(string block)
    {
        // Map the rendered term names back to their seeded ids to assert similarity order.
        var nameToId = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["revenue"] = 1,
            ["active user"] = 2,
            ["churn"] = 3
        };

        return block
            .Split('\n')
            .Where(x => x.StartsWith("- **", StringComparison.Ordinal))
            .Select(x => x["- **".Length..])
            .Select(x => x[..x.IndexOf("**", StringComparison.Ordinal)])
            .Select(x => nameToId[x])
            .ToList();
    }

    private static McpGlossaryTerm NewTerm(
        int id, string term, string definition, bool isActive,
        string? schema = null, string? table = null, string? column = null, string? metric = null) =>
        new()
        {
            Id = id,
            ProjectId = ProjectId,
            Term = term,
            Definition = definition,
            TargetSchema = schema,
            TargetTable = table,
            TargetColumn = column,
            MetricExpression = metric,
            IsActive = isActive
        };

    private static McpEmbedding NewGlossaryEmbedding(int ownerId, float[] vector) =>
        new()
        {
            Id = 500 + ownerId,
            DataSourceId = 0,
            ProjectId = ProjectId,
            OwnerType = McpEmbeddingOwnerType.GlossaryTerm,
            OwnerId = ownerId,
            EmbeddingBytes = EmbeddingCodec.ToBytes(vector),
            Model = "bge-small-en-v1.5",
            Dimensions = 384,
            EmbeddingVersion = 1
        };

    private static KnowledgeGraphService BuildService(
        IBeaconEmbeddingService embedder,
        List<McpGlossaryTerm> terms,
        List<McpEmbedding> embeddings,
        bool semanticEnabled,
        int glossaryTopK,
        LogRecorder? logs = null)
    {
        var links = new List<ProjectDataSource>
        {
            new() { Id = 1, ProjectId = ProjectId, DataSourceId = DataSourceId }
        };

        var context = new GlossaryInjectionContext(
            BuildDbSet(links).Object,
            BuildDbSet(embeddings).Object,
            BuildDbSet(terms).Object);

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var settings = SettingsProviderMock.Create();
        settings
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData
            {
                EnableSemanticRetrieval = semanticEnabled,
                GlossaryTopK = glossaryTopK
            });

        return new KnowledgeGraphService(
            factory.Object,
            settings.Object,
            SettingsProviderMock.Unshared(settings.Object),
            embedder,
            Mock.Of<ISchemaGraphService>(x => x.GetGraphAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())
                == Task.FromResult(SchemaGraph.Build(Array.Empty<TableMetadataDto>(), Array.Empty<SchemaRelationshipEdge>()))),
            Mock.Of<IValueGroundingService>(x => x.BuildValueMatchesBlockAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ValueGroundingTable>>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>())
                == Task.FromResult("")),
            TestConfiguredDatabases.None,
            new QueryGuardrailService(),
            logs?.For<KnowledgeGraphService>() ?? NullLogger<KnowledgeGraphService>.Instance);
    }

    private static Mock<DbSet<T>> BuildDbSet<T>(IEnumerable<T> data) where T : class
    {
        var queryable = data.AsQueryable();
        var set = new Mock<DbSet<T>>();
        set.As<IAsyncEnumerable<T>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<T>(data.GetEnumerator()));
        set.As<IQueryable<T>>().Setup(x => x.Provider).Returns(new TestAsyncQueryProvider<T>(queryable.Provider));
        set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(queryable.Expression);
        set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
        set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());
        return set;
    }

    /// <summary>
    /// A <see cref="BeaconContext"/> whose project-link / embedding / glossary sets resolve to the supplied
    /// mocked sets, and whose <c>Database.ProviderName</c> reports a NON-Npgsql provider so the deterministic
    /// in-memory cosine retrieval path is exercised (the raw pgvector path needs a live DB).
    /// </summary>
    private sealed class GlossaryInjectionContext : BeaconContext
    {
        private static readonly DbContextOptions<GlossaryInjectionContext> Options =
            new DbContextOptionsBuilder<GlossaryInjectionContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<ProjectDataSource> _links;
        private readonly DbSet<McpEmbedding> _embeddings;
        private readonly DbSet<McpGlossaryTerm> _terms;
        private DatabaseFacade? _database;

        public GlossaryInjectionContext(
            DbSet<ProjectDataSource> links, DbSet<McpEmbedding> embeddings, DbSet<McpGlossaryTerm> terms)
            : base(Options, "beacon")
        {
            _links = links;
            _embeddings = embeddings;
            _terms = terms;
        }

        public override DatabaseFacade Database => _database ??= new NonNpgsqlDatabaseFacade(this);

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(ProjectDataSource))
            {
                return (DbSet<TEntity>)(object)_links;
            }

            if (typeof(TEntity) == typeof(McpEmbedding))
            {
                return (DbSet<TEntity>)(object)_embeddings;
            }

            if (typeof(TEntity) == typeof(McpGlossaryTerm))
            {
                return (DbSet<TEntity>)(object)_terms;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    /// <summary>Minimal <see cref="BeaconContext"/> exposing only the glossary set for the CRUD handler tests.</summary>
    private sealed class GlossaryHandlerContext : BeaconContext, IObservableSaves
    {
        private static readonly DbContextOptions<GlossaryHandlerContext> Options =
            new DbContextOptionsBuilder<GlossaryHandlerContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<McpGlossaryTerm> _terms;
        private readonly DbSet<ProjectDataSource> _links;

        // By default the test project links the test data source, as the consistency rule requires.
        public GlossaryHandlerContext(DbSet<McpGlossaryTerm> terms, List<ProjectDataSource>? links = null) : base(Options, "beacon")
        {
            _terms = terms;
            _links = BuildDbSet(links ?? [new ProjectDataSource { ProjectId = ProjectId, DataSourceId = DataSourceId }]).Object;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpGlossaryTerm))
            {
                return (DbSet<TEntity>)(object)_terms;
            }

            if (typeof(TEntity) == typeof(ProjectDataSource))
            {
                return (DbSet<TEntity>)(object)_links;
            }

            return base.Set<TEntity>();
        }

        public event Action? Saving;

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Saving?.Invoke();

            return Task.FromResult(0);
        }
    }

    private sealed class NonNpgsqlDatabaseFacade : DatabaseFacade
    {
        public NonNpgsqlDatabaseFacade(DbContext context) : base(context) { }

        public override string? ProviderName => "Microsoft.EntityFrameworkCore.SqlServer";
    }

    private sealed class UnavailableEmbeddingService : IBeaconEmbeddingService
    {
        public int Dimensions => 384;

        public bool IsAvailable => false;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
            throw new InvalidOperationException("Embeddings unavailable");

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
            throw new InvalidOperationException("Embeddings unavailable");
    }
}
