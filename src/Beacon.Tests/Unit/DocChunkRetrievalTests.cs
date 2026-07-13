using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Tier-3 ⑨ regression guard for <see cref="KnowledgeGraphService.GetRelevantDocChunksAsync"/>: the query
/// must be embedded the SAME way doc chunks are indexed (RAW, not masked). Masking is a SQL-exemplar
/// technique that strips literals/numbers — using it on prose RAG puts the query vector in a different
/// representation region than the raw chunk vectors and silently degrades recall (Stage-1 finding, 2026-07-13).
/// This test seeds one chunk whose stored vector is Embed(raw question) and a decoy whose vector is
/// Embed(Mask(question)); the raw-match chunk must win. It PASSES with raw-question embedding and FAILS with
/// the masked-question bug. Runs the in-memory cosine path (non-Npgsql provider) with the deterministic
/// FakeEmbeddingService — no DB, no forbidden UseInMemoryDatabase (§4.7).
/// </summary>
[TestFixture]
public class DocChunkRetrievalTests
{
    private const int ProjectId = 3;
    private const string Question = "How many orders were placed in 2023?";
    private const string RawMatchText = "RAW_MATCH chunk";
    private const string MaskedMatchText = "MASKED_MATCH chunk";

    [Test]
    public async Task GetRelevantDocChunks_EmbedsRawQuestion_MatchingRawIndexedChunks_NotMasked()
    {
        var embedder = new FakeEmbeddingService();

        // Chunk 1's stored vector == Embed(RAW question); chunk 2's == Embed(Mask(question)). Only the
        // query-embedding representation decides which is the exact (cosine 1.0) match.
        var rawQuestionVector = await embedder.EmbedAsync(Question, CancellationToken.None);
        var maskedQuestionVector = await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(Question), CancellationToken.None);

        var chunks = new List<McpDocChunk>
        {
            new() { Id = 1, ProjectId = ProjectId, SourceSectionId = 10, ChunkText = RawMatchText, SortOrder = 0 },
            new() { Id = 2, ProjectId = ProjectId, SourceSectionId = 10, ChunkText = MaskedMatchText, SortOrder = 1 }
        };
        var embeddings = new List<McpEmbedding>
        {
            NewDocChunkEmbedding(ownerId: 1, rawQuestionVector),
            NewDocChunkEmbedding(ownerId: 2, maskedQuestionVector)
        };

        var service = BuildService(embedder, chunks, embeddings);

        var hits = await service.GetRelevantDocChunksAsync(ProjectId, Question, topK: 1, CancellationToken.None);

        hits.Should().ContainSingle();
        hits[0].ChunkText.Should().Be(RawMatchText,
            "the query must be embedded RAW to match raw-indexed chunks — masking it would rank the MASKED_MATCH decoy first");
    }

    [Test]
    public async Task GetRelevantDocChunks_WhenEmbedderUnavailable_ReturnsEmpty()
    {
        var chunks = new List<McpDocChunk>
        {
            new() { Id = 1, ProjectId = ProjectId, SourceSectionId = 10, ChunkText = RawMatchText, SortOrder = 0 }
        };
        var embeddings = new List<McpEmbedding> { NewDocChunkEmbedding(1, new float[384]) };

        var service = BuildService(new UnavailableEmbeddingService(), chunks, embeddings);

        var hits = await service.GetRelevantDocChunksAsync(ProjectId, Question, topK: 5, CancellationToken.None);

        hits.Should().BeEmpty("no embedder => the caller falls back to char-truncated documentation");
    }

    private static McpEmbedding NewDocChunkEmbedding(int ownerId, float[] vector) =>
        new()
        {
            Id = 700 + ownerId,
            DataSourceId = 0,
            ProjectId = ProjectId,
            OwnerType = McpEmbeddingOwnerType.DocChunk,
            OwnerId = ownerId,
            EmbeddingBytes = EmbeddingCodec.ToBytes(vector),
            Model = "bge-small-en-v1.5",
            Dimensions = 384,
            EmbeddingVersion = 1
        };

    private static KnowledgeGraphService BuildService(
        IBeaconEmbeddingService embedder, List<McpDocChunk> chunks, List<McpEmbedding> embeddings)
    {
        var context = new DocChunkContext(BuildDbSet(embeddings).Object, BuildDbSet(chunks).Object);

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var settings = new Mock<IMcpSettingsProvider>();
        settings
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData { EnableSemanticRetrieval = true });

        return new KnowledgeGraphService(
            factory.Object, settings.Object, embedder, NullLogger<KnowledgeGraphService>.Instance);
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

    private sealed class DocChunkContext : BeaconContext
    {
        private static readonly DbContextOptions<DocChunkContext> Options =
            new DbContextOptionsBuilder<DocChunkContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<McpEmbedding> _embeddings;
        private readonly DbSet<McpDocChunk> _chunks;
        private DatabaseFacade? _database;

        public DocChunkContext(DbSet<McpEmbedding> embeddings, DbSet<McpDocChunk> chunks) : base(Options, "beacon")
        {
            _embeddings = embeddings;
            _chunks = chunks;
        }

        public override DatabaseFacade Database => _database ??= new NonNpgsqlDatabaseFacade(this);

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpEmbedding))
            {
                return (DbSet<TEntity>)(object)_embeddings;
            }

            if (typeof(TEntity) == typeof(McpDocChunk))
            {
                return (DbSet<TEntity>)(object)_chunks;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
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
