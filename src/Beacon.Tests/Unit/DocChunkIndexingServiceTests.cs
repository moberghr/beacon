using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Ai;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Unit coverage for the T3-B3 doc-chunk indexing job (⑨ chunking + ⑩ contextual retrieval). Exercises the
/// real chunk→(optional blurb)→embed→upsert logic against a mocked <see cref="BeaconContext"/> backed by the
/// async-queryable doubles (no DB, no forbidden <c>UseInMemoryDatabase</c> — §4.7). Proves: (a) with
/// contextual retrieval OFF a project's single section producing N chunks yields N <see cref="McpDocChunk"/>
/// + N <see cref="McpEmbedding"/> (OwnerType=DocChunk) rows, embeds the RAW chunk text, and never touches the
/// LLM; (b) with contextual retrieval ON the LLM is called once per chunk, the blurb is stored on the chunk
/// AND prepended to the embedded text; and (c) an unavailable embedder is a no-op that opens no context and
/// makes no LLM call.
/// </summary>
[TestFixture]
public class DocChunkIndexingServiceTests
{
    private const int ExpectedByteLength = 384 * sizeof(float);
    private const int ProjectId = 1;
    private const int SectionId = 50;

    // Seven sentences so the default-ish window/overlap used by the tests yields several chunks.
    private const string SectionContent =
        "Alpha one. Beta two. Gamma three. Delta four. Epsilon five. Zeta six. Eta seven.";

    [Test]
    public async Task ReindexAsync_ContextualRetrievalOff_CreatesChunksAndEmbeddings_WithoutCallingLlm()
    {
        var settings = new McpSettingsData
        {
            EnableSemanticRetrieval = true,
            EnableContextualRetrieval = false,
            DocChunkWindowSentences = 3,
            DocChunkOverlapSentences = 1
        };

        var expectedChunks = DocumentChunker.Chunk(SectionContent, settings.DocChunkWindowSentences, settings.DocChunkOverlapSentences);
        // Guard the fixture itself: the window/overlap must produce more than one chunk or the test proves nothing.
        expectedChunks.Count.Should().BeGreaterThan(1);

        var embeddings = new CapturingEmbeddingService();
        var llm = new Mock<ILlmProvider>();
        var capturedChunks = new List<McpDocChunk>();
        var capturedEmbeddings = new List<McpEmbedding>();

        var service = BuildService(
            settings, embeddings, llm.Object,
            existingChunks: [], existingEmbeddings: [],
            capturedChunks, capturedEmbeddings, removedChunks: [], removedEmbeddings: []);

        await service.ReindexAsync(CancellationToken.None);

        // One McpDocChunk per produced chunk, in sort order, raw chunk text, no blurb.
        capturedChunks.Should().HaveCount(expectedChunks.Count);
        capturedChunks.Select(x => x.ChunkText).Should().BeEquivalentTo(expectedChunks);
        capturedChunks.Should().OnlyContain(x => x.ProjectId == ProjectId && x.SourceSectionId == SectionId && x.ContextualBlurb == null);
        capturedChunks.Select(x => x.SortOrder).Should().BeEquivalentTo(Enumerable.Range(0, expectedChunks.Count));

        // One McpEmbedding per chunk, DocChunk-typed, project-scoped, DataSourceId null (project-scoped rows
        // no longer carry the magic-sentinel 0), model/version/dims set, and keyed by the DB-assigned chunk id.
        capturedEmbeddings.Should().HaveCount(expectedChunks.Count);
        capturedEmbeddings.Should().OnlyContain(x =>
            x.OwnerType == McpEmbeddingOwnerType.DocChunk
            && x.ProjectId == ProjectId
            && x.DataSourceId == null
            && x.Dimensions == 384
            && x.EmbeddingVersion == 1
            && x.Model == "bge-small-en-v1.5"
            && x.EmbeddingBytes.Length == ExpectedByteLength);
        capturedEmbeddings.Select(x => x.OwnerId).Should().BeEquivalentTo(capturedChunks.Select(x => x.Id));

        // Contextual retrieval OFF → the RAW chunk text is embedded (no blurb prefix) and the LLM is untouched.
        embeddings.BatchTexts.Should().BeEquivalentTo(expectedChunks);
        llm.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ReindexAsync_ContextualRetrievalOn_CallsLlmAndStoresAndPrependsBlurb()
    {
        const string blurb = "This chunk covers the greek-letter enumeration in the overview section.";

        var settings = new McpSettingsData
        {
            EnableSemanticRetrieval = true,
            EnableContextualRetrieval = true,
            DocChunkWindowSentences = 3,
            DocChunkOverlapSentences = 1
        };

        var expectedChunks = DocumentChunker.Chunk(SectionContent, settings.DocChunkWindowSentences, settings.DocChunkOverlapSentences);
        expectedChunks.Count.Should().BeGreaterThan(1);

        var embeddings = new CapturingEmbeddingService();
        var llm = new Mock<ILlmProvider>();
        llm.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = blurb });

        var capturedChunks = new List<McpDocChunk>();
        var capturedEmbeddings = new List<McpEmbedding>();

        var service = BuildService(
            settings, embeddings, llm.Object,
            existingChunks: [], existingEmbeddings: [],
            capturedChunks, capturedEmbeddings, removedChunks: [], removedEmbeddings: []);

        await service.ReindexAsync(CancellationToken.None);

        // The LLM is invoked once per chunk (whole-section + chunk → situating blurb).
        llm.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(expectedChunks.Count));

        // The blurb is STORED on every chunk row.
        capturedChunks.Should().HaveCount(expectedChunks.Count);
        capturedChunks.Should().OnlyContain(x => x.ContextualBlurb == blurb);

        // The blurb is PREPENDED to the embedded text (blurb + chunk) for each chunk, so the vector carries
        // the situating context (Anthropic contextual retrieval).
        embeddings.BatchTexts.Should().HaveCount(expectedChunks.Count);
        for (var i = 0; i < expectedChunks.Count; i++)
        {
            embeddings.BatchTexts[i].Should().StartWith(blurb);
            embeddings.BatchTexts[i].Should().Contain(expectedChunks[i]);
        }

        capturedEmbeddings.Should().HaveCount(expectedChunks.Count);
        capturedEmbeddings.Should().OnlyContain(x => x.OwnerType == McpEmbeddingOwnerType.DocChunk && x.ProjectId == ProjectId);
    }

    [Test]
    public async Task ReindexAsync_WhenEmbedderUnavailable_IsNoOpAndTouchesNoDatabaseOrLlm()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict);
        var llm = new Mock<ILlmProvider>(MockBehavior.Strict);
        var settings = new Mock<IMcpSettingsProvider>(MockBehavior.Strict);

        var service = new DocChunkIndexingService(
            factory.Object,
            new UnavailableEmbeddingService(),
            settings.Object,
            llm.Object,
            NullLogger<DocChunkIndexingService>.Instance);

        await service.ReindexAsync(CancellationToken.None);

        factory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
        llm.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        settings.Verify(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ReindexAsync_ContextualRetrievalOn_AllBlurbsFail_EmbedsRawChunks_NeverDrops_AndLogsError()
    {
        // Contextual retrieval ON but the provider is down for every chunk. The raw chunk must still be
        // embedded (never dropped, null blurb) AND the all-failed condition must be surfaced as an Error so a
        // total outage is not hidden behind a green job (distinct from contextual retrieval being disabled).
        var settings = new McpSettingsData
        {
            EnableSemanticRetrieval = true,
            EnableContextualRetrieval = true,
            DocChunkWindowSentences = 3,
            DocChunkOverlapSentences = 1
        };

        var expectedChunks = DocumentChunker.Chunk(SectionContent, settings.DocChunkWindowSentences, settings.DocChunkOverlapSentences);
        expectedChunks.Count.Should().BeGreaterThan(1);

        var embeddings = new CapturingEmbeddingService();
        var llm = new Mock<ILlmProvider>();
        llm.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM provider unavailable"));

        var capturedChunks = new List<McpDocChunk>();
        var capturedEmbeddings = new List<McpEmbedding>();
        var logger = new ListLogger<DocChunkIndexingService>();

        var service = BuildService(
            settings, embeddings, llm.Object,
            existingChunks: [], existingEmbeddings: [],
            capturedChunks, capturedEmbeddings, removedChunks: [], removedEmbeddings: [], logger);

        await service.ReindexAsync(CancellationToken.None);

        // A blurb was attempted per chunk and threw every time.
        llm.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(expectedChunks.Count));

        // No chunk dropped; every row stored a NULL blurb (raw-chunk fallback).
        capturedChunks.Should().HaveCount(expectedChunks.Count);
        capturedChunks.Should().OnlyContain(x => x.ContextualBlurb == null);
        capturedChunks.Select(x => x.ChunkText).Should().BeEquivalentTo(expectedChunks);

        // The RAW chunk text was embedded (no blurb prefix), and one embedding row exists per chunk.
        embeddings.BatchTexts.Should().BeEquivalentTo(expectedChunks);
        capturedEmbeddings.Should().HaveCount(expectedChunks.Count);

        // The all-failed outage is surfaced as an Error.
        logger.Entries.Should().Contain(x =>
            x.Level == LogLevel.Error && x.Message.Contains("blurb generation failed for all"));
    }

    [Test]
    public async Task ReindexAsync_PrunesStaleChunk_AndUpdatesMatchingChunkInPlace_NotDuplicated()
    {
        // Idempotent re-index: an existing chunk whose (SourceSectionId, SortOrder) is still produced is
        // mutated in place; one whose key is no longer produced is removed along with its embedding.
        var settings = new McpSettingsData
        {
            EnableSemanticRetrieval = true,
            EnableContextualRetrieval = false,
            DocChunkWindowSentences = 3,
            DocChunkOverlapSentences = 1
        };

        var expectedChunks = DocumentChunker.Chunk(SectionContent, settings.DocChunkWindowSentences, settings.DocChunkOverlapSentences);
        expectedChunks.Count.Should().BeGreaterThan(1);

        // Still produced (index 0) → updated in place.
        var keptChunk = new McpDocChunk
        {
            Id = 901,
            ProjectId = ProjectId,
            SourceSectionId = SectionId,
            SortOrder = 0,
            ChunkText = "OUTDATED text that must be overwritten"
        };
        // No longer produced (SortOrder 999 is beyond the produced range) → pruned with its embedding.
        var staleChunk = new McpDocChunk
        {
            Id = 900,
            ProjectId = ProjectId,
            SourceSectionId = SectionId,
            SortOrder = 999,
            ChunkText = "STALE chunk no longer produced by the current content"
        };

        var keptEmbedding = NewExistingDocChunkEmbedding(id: 811, ownerId: keptChunk.Id);
        var staleEmbedding = NewExistingDocChunkEmbedding(id: 800, ownerId: staleChunk.Id);

        var embeddings = new CapturingEmbeddingService();
        var capturedChunks = new List<McpDocChunk>();
        var capturedEmbeddings = new List<McpEmbedding>();
        var removedChunks = new List<McpDocChunk>();
        var removedEmbeddings = new List<McpEmbedding>();

        var service = BuildService(
            settings, embeddings, new Mock<ILlmProvider>().Object,
            existingChunks: [staleChunk, keptChunk],
            existingEmbeddings: [staleEmbedding, keptEmbedding],
            capturedChunks, capturedEmbeddings, removedChunks, removedEmbeddings);

        await service.ReindexAsync(CancellationToken.None);

        // The stale chunk + its embedding are pruned (RemoveRange captured).
        removedChunks.Select(x => x.Id).Should().Equal(staleChunk.Id);
        removedEmbeddings.Select(x => x.OwnerId).Should().Equal(staleChunk.Id);

        // The matching chunk is UPDATED IN PLACE (text now equals the first produced chunk) and NOT duplicated
        // (no freshly-added chunk carries SortOrder 0; the new adds are exactly the remaining indexes).
        keptChunk.ChunkText.Should().Be(expectedChunks[0]);
        capturedChunks.Should().NotContain(x => x.SortOrder == 0);
        capturedChunks.Should().HaveCount(expectedChunks.Count - 1);
        capturedChunks.Select(x => x.SortOrder).Should().BeEquivalentTo(Enumerable.Range(1, expectedChunks.Count - 1));

        // The kept chunk's embedding is reused (updated in place), not re-added.
        capturedEmbeddings.Select(x => x.OwnerId).Should().NotContain(keptChunk.Id);
    }

    private static McpEmbedding NewExistingDocChunkEmbedding(int id, int ownerId) =>
        new()
        {
            Id = id,
            DataSourceId = null,
            ProjectId = ProjectId,
            OwnerType = McpEmbeddingOwnerType.DocChunk,
            OwnerId = ownerId,
            EmbeddingBytes = new byte[ExpectedByteLength],
            Model = "bge-small-en-v1.5",
            Dimensions = 384,
            EmbeddingVersion = 1
        };

    private static DocChunkIndexingService BuildService(
        McpSettingsData settings,
        IBeaconEmbeddingService embeddingService,
        ILlmProvider llmProvider,
        List<McpDocChunk> existingChunks,
        List<McpEmbedding> existingEmbeddings,
        List<McpDocChunk> capturedChunks,
        List<McpEmbedding> capturedEmbeddings,
        List<McpDocChunk> removedChunks,
        List<McpEmbedding> removedEmbeddings,
        ILogger<DocChunkIndexingService>? logger = null)
    {
        var documentation = new ProjectDocumentation
        {
            Id = 1000,
            ProjectId = ProjectId,
            GeneratedByModel = "test",
            GeneratedAt = DateTime.UtcNow
        };

        var section = new ProjectDocumentationSection
        {
            Id = SectionId,
            ProjectDocumentationId = documentation.Id,
            SectionType = ProjectDocSectionType.ProjectOverview,
            Title = "Project Overview",
            Content = SectionContent,
            SortOrder = 0,
            Documentation = documentation
        };

        var docSet = BuildDbSet(new List<ProjectDocumentation> { documentation });
        var sectionSet = BuildDbSet(new List<ProjectDocumentationSection> { section });

        var chunkSet = BuildDbSet(existingChunks);
        chunkSet
            .Setup(x => x.AddRangeAsync(It.IsAny<IEnumerable<McpDocChunk>>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<McpDocChunk> entities, CancellationToken _) =>
            {
                capturedChunks.AddRange(entities);
                return Task.CompletedTask;
            });
        chunkSet
            .Setup(x => x.RemoveRange(It.IsAny<IEnumerable<McpDocChunk>>()))
            .Callback((IEnumerable<McpDocChunk> entities) => removedChunks.AddRange(entities));

        var embeddingSet = BuildDbSet(existingEmbeddings);
        embeddingSet
            .Setup(x => x.AddRangeAsync(It.IsAny<IEnumerable<McpEmbedding>>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<McpEmbedding> entities, CancellationToken _) =>
            {
                capturedEmbeddings.AddRange(entities);
                return Task.CompletedTask;
            });
        embeddingSet
            .Setup(x => x.RemoveRange(It.IsAny<IEnumerable<McpEmbedding>>()))
            .Callback((IEnumerable<McpEmbedding> entities) => removedEmbeddings.AddRange(entities));

        // The reindex also runs a glossary pass (⑪): these fixtures seed no glossary terms, so an empty set
        // makes that pass a no-op and leaves the doc-chunk assertions untouched.
        var glossarySet = BuildDbSet(new List<McpGlossaryTerm>());

        // SaveChanges assigns DB-generated ids to newly-added chunks (Id == 0) so the second unit of work
        // can key each chunk's embedding by its id — the real chunk-id chicken/egg the service handles with
        // two SaveChanges. capturedChunks holds the same object references the service embeds against.
        var context = new IndexingTestContext(
            docSet.Object, sectionSet.Object, chunkSet.Object, embeddingSet.Object, glossarySet.Object, capturedChunks);

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var settingsProvider = new Mock<IMcpSettingsProvider>();
        settingsProvider
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        return new DocChunkIndexingService(
            factory.Object,
            embeddingService,
            settingsProvider.Object,
            llmProvider,
            logger ?? NullLogger<DocChunkIndexingService>.Instance);
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
    /// A <see cref="BeaconContext"/> whose documentation / section / chunk / embedding sets resolve to the
    /// supplied mocked sets. SaveChanges is short-circuited (no DB) but assigns sequential ids to freshly
    /// added chunks so the second unit of work can key embeddings by the chunk id.
    /// </summary>
    private sealed class IndexingTestContext : BeaconContext
    {
        private static readonly DbContextOptions<IndexingTestContext> Options =
            new DbContextOptionsBuilder<IndexingTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<ProjectDocumentation> _documentations;
        private readonly DbSet<ProjectDocumentationSection> _sections;
        private readonly DbSet<McpDocChunk> _chunks;
        private readonly DbSet<McpEmbedding> _embeddings;
        private readonly DbSet<McpGlossaryTerm> _glossaryTerms;
        private readonly List<McpDocChunk> _addedChunks;
        private int _nextChunkId = 1;

        public IndexingTestContext(
            DbSet<ProjectDocumentation> documentations,
            DbSet<ProjectDocumentationSection> sections,
            DbSet<McpDocChunk> chunks,
            DbSet<McpEmbedding> embeddings,
            DbSet<McpGlossaryTerm> glossaryTerms,
            List<McpDocChunk> addedChunks) : base(Options, "beacon")
        {
            _documentations = documentations;
            _sections = sections;
            _chunks = chunks;
            _embeddings = embeddings;
            _glossaryTerms = glossaryTerms;
            _addedChunks = addedChunks;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(ProjectDocumentation))
            {
                return (DbSet<TEntity>)(object)_documentations;
            }

            if (typeof(TEntity) == typeof(ProjectDocumentationSection))
            {
                return (DbSet<TEntity>)(object)_sections;
            }

            if (typeof(TEntity) == typeof(McpDocChunk))
            {
                return (DbSet<TEntity>)(object)_chunks;
            }

            if (typeof(TEntity) == typeof(McpEmbedding))
            {
                return (DbSet<TEntity>)(object)_embeddings;
            }

            if (typeof(TEntity) == typeof(McpGlossaryTerm))
            {
                return (DbSet<TEntity>)(object)_glossaryTerms;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => SaveChangesAsync().GetAwaiter().GetResult();

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            foreach (var chunk in _addedChunks)
            {
                if (chunk.Id == 0)
                {
                    chunk.Id = _nextChunkId++;
                }
            }

            return Task.FromResult(0);
        }
    }

    /// <summary>Records the batch texts it is asked to embed, then produces the deterministic fake vectors.</summary>
    private sealed class CapturingEmbeddingService : IBeaconEmbeddingService
    {
        private readonly FakeEmbeddingService _inner = new();

        public List<string> BatchTexts { get; } = [];

        public int Dimensions => _inner.Dimensions;

        public bool IsAvailable => true;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => _inner.EmbedAsync(text, ct);

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            BatchTexts.AddRange(texts);
            return _inner.EmbedBatchAsync(texts, ct);
        }
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

    /// <summary>Captures every log entry (level + formatted message) so the all-failed Error can be asserted.</summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
