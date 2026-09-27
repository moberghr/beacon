using Beacon.Core;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.HostDocs;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.HostDocs;

[TestFixture]
public class HostDocsSynchronizerTests
{
    private const string ProjectName = "Netgiro";

    private string _root = null!;
    private DocsStore _store = null!;
    private Mock<IDocChunkIndexingService> _indexer = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "beacon-hostdocs-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "docs", "wiki", "50-data"));
        Write("README.md", "---\ntitle: Netgiro Platform\n---\n# Netgiro\n\nA consumer financing platform. Merchants get paid up front.");
        Write("50-data/contexts.md", "# Contexts\n\nThe NetgiroContext holds loans. Claims live elsewhere.");

        _store = new DocsStore();
        _store.Projects.Add(new Project { Id = 7, Name = ProjectName, HostManagedKey = HostProjectResolver.KeyFor(ProjectName) });
        _indexer = new Mock<IDocChunkIndexingService>();
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task FirstSync_ImportsDocuments_WritesChunks_InOneSave_AndReindexesOnce()
    {
        var outcome = await SyncAsync();

        outcome.Should().BeEquivalentTo(new { ProjectId = 7, Added = 2, Updated = 0, Archived = 0, Unchanged = 0, Skipped = 0 });
        _store.SaveCount.Should().Be(1, "an existing project is one unit of work");

        _store.Documents.Select(x => (x.Path, x.Title)).Should().BeEquivalentTo(
            [("50-data/contexts.md", "Contexts"), ("README.md", "Netgiro Platform")]);
        _store.Documents.Should().OnlyContain(x => x.ProjectId == 7 && x.SourceKey == "docs:dir:docs/wiki");
        _store.Documents.Single(x => x.Path == "README.md").FrontmatterJson.Should().Be("""{"title":"Netgiro Platform"}""");
        _store.Documents.Single(x => x.Path == "README.md").Content.Should().StartWith("# Netgiro");

        _store.Chunks.Should().NotBeEmpty();
        _store.Chunks.Should().OnlyContain(x => x.ProjectId == 7 && x.SourceSectionId == 0 && x.ImportedDocumentId != null);
        outcome.ChunksWritten.Should().Be(_store.Chunks.Count);

        _indexer.Verify(x => x.ReindexProjectAsync(7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SecondSync_Unchanged_SkipsByHash_WithoutSaveOrReindex()
    {
        await SyncAsync();
        var chunkIds = _store.Chunks.Select(x => x.Id).ToList();
        _indexer.Invocations.Clear();

        var outcome = await SyncAsync();

        outcome.Should().BeEquivalentTo(new { Added = 0, Updated = 0, Archived = 0, Unchanged = 2, ChunksWritten = 0 });
        _store.SaveCount.Should().Be(1, "an unchanged sync must not write");
        _store.Chunks.Select(x => x.Id).Should().Equal(chunkIds);
        _indexer.Verify(x => x.ReindexProjectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ChangedDocument_IsUpdated_AndOnlyItsChunksAndVectorsAreReplaced()
    {
        await SyncAsync();
        var readme = _store.Documents.Single(x => x.Path == "README.md");
        var contexts = _store.Documents.Single(x => x.Path == "50-data/contexts.md");
        var untouchedChunkIds = _store.Chunks.Where(x => x.ImportedDocumentId == contexts.Id).Select(x => x.Id).ToList();
        var staleChunk = _store.Chunks.First(x => x.ImportedDocumentId == readme.Id);
        _store.Embeddings.Add(new McpEmbedding { Id = 1, ProjectId = 7, OwnerType = McpEmbeddingOwnerType.DocChunk, OwnerId = staleChunk.Id, EmbeddingBytes = [], Model = "m" });
        _store.Embeddings.Add(new McpEmbedding { Id = 2, ProjectId = 7, OwnerType = McpEmbeddingOwnerType.DocChunk, OwnerId = untouchedChunkIds[0], EmbeddingBytes = [], Model = "m" });
        var previousHash = readme.ContentHash;
        _indexer.Invocations.Clear();

        Write("README.md", "# Netgiro\n\nRewritten overview. Investors fund the loans.");
        var outcome = await SyncAsync();

        outcome.Should().BeEquivalentTo(new { Added = 0, Updated = 1, Archived = 0, Unchanged = 1 });
        _store.Documents.Should().HaveCount(2);
        readme.ContentHash.Should().NotBe(previousHash);
        readme.Title.Should().Be("Netgiro", "the frontmatter title is gone, so the first heading wins");
        readme.FrontmatterJson.Should().BeNull();

        _store.Chunks.Should().NotContain(x => x.Id == staleChunk.Id);
        _store.Chunks.Where(x => x.ImportedDocumentId == readme.Id).Should().ContainSingle(x => x.ChunkText.Contains("Investors"));
        _store.Chunks.Where(x => x.ImportedDocumentId == contexts.Id).Select(x => x.Id).Should().Equal(untouchedChunkIds);
        _store.Embeddings.Select(x => x.Id).Should().Equal([2], "only the changed document's vectors are dropped");
        _indexer.Verify(x => x.ReindexProjectAsync(7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task RemovedFile_IsArchived_AndItsChunksDropped_ThenRestoredOnReturn()
    {
        await SyncAsync();
        var contexts = _store.Documents.Single(x => x.Path == "50-data/contexts.md");
        File.Delete(Path.Combine(_root, "docs", "wiki", "50-data", "contexts.md"));

        var removed = await SyncAsync();

        removed.Archived.Should().Be(1);
        contexts.ArchivedTime.Should().NotBeNull();
        _store.Chunks.Should().NotContain(x => x.ImportedDocumentId == contexts.Id);

        Write("50-data/contexts.md", "# Contexts\n\nThe NetgiroContext holds loans. Claims live elsewhere.");
        var restored = await SyncAsync();

        restored.Updated.Should().Be(1, "a returning file un-archives its row instead of inserting a duplicate");
        _store.Documents.Should().HaveCount(2);
        contexts.ArchivedTime.Should().BeNull();
        _store.Chunks.Should().Contain(x => x.ImportedDocumentId == contexts.Id);
    }

    [Test]
    public async Task MissingProject_IsCreated_InItsOwnUnitOfWork()
    {
        _store.Projects.Clear();

        var outcome = await SyncAsync();

        _store.Projects.Should().ContainSingle(x => x.Name == ProjectName);
        outcome.ProjectId.Should().Be(_store.Projects[0].Id);
        _store.Documents.Should().OnlyContain(x => x.ProjectId == outcome.ProjectId);
        _store.Chunks.Should().OnlyContain(x => x.ProjectId == outcome.ProjectId);
        _store.SaveCount.Should().Be(2);
    }

    [Test]
    public async Task UserProjectWithTheSameName_NeverReceivesTheHostDocuments()
    {
        _store.Projects.Clear();
        _store.Projects.Add(new Project { Id = 3, Name = ProjectName });

        var outcome = await SyncAsync();

        outcome.ProjectId.Should().NotBe(3);
        _store.Projects.Single(x => x.Id == outcome.ProjectId).HostManagedKey.Should().Be("host:netgiro");
        _store.Documents.Should().NotContain(x => x.ProjectId == 3);
    }

    [Test]
    public async Task CaseOnlyRename_UpdatesTheExistingRow_InsteadOfInsertingADuplicate()
    {
        await SyncAsync();
        var readme = _store.Documents.Single(x => x.Path == "README.md");
        readme.Path = "readme.md";

        var outcome = await SyncAsync();

        outcome.Should().BeEquivalentTo(new { Added = 0, Updated = 1, Archived = 0, Unchanged = 1 });
        _store.Documents.Should().HaveCount(2, "SQL Server's unique index is case-insensitive, so an insert would fail startup");
        readme.Path.Should().Be("README.md", "the row takes the file's current casing");
        readme.ArchivedTime.Should().BeNull();
    }

    [Test]
    public void DocumentKeyComparer_IgnoresCase_LikeTheSqlServerIndex()
    {
        var keys = new HashSet<(string SourceKey, string Path)>(DocumentKeyComparer.Instance) { ("docs:dir:docs/wiki", "Guide.md") };

        keys.Add(("docs:dir:docs/WIKI", "guide.md")).Should().BeFalse();
        keys.Add(("docs:dir:docs/wiki", "guide2.md")).Should().BeTrue();
    }

    [Test]
    public async Task OtherProjectsDocuments_AreNeverTouched()
    {
        var foreign = new ProjectImportedDocument
        {
            Id = 50,
            ProjectId = 99,
            SourceKey = "docs:dir:docs/wiki",
            Path = "README.md",
            Title = "Other",
            ContentHash = "x",
            Content = "Other project."
        };
        _store.Documents.Add(foreign);

        await SyncAsync();

        foreign.ArchivedTime.Should().BeNull();
        foreign.Title.Should().Be("Other");
        _store.Documents.Where(x => x.ProjectId == 7).Should().HaveCount(2);
    }

    [Test]
    public async Task EmbeddingReindexFailure_DoesNotFailTheSync()
    {
        _indexer
            .Setup(x => x.ReindexProjectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("embedder down"));

        var outcome = await SyncAsync();

        outcome.Added.Should().Be(2);
        _store.Chunks.Should().NotBeEmpty();
    }

    [Test]
    public async Task NoIndexer_StillWritesKeywordSearchableChunks()
    {
        var synchronizer = BuildSynchronizer(indexer: null);

        await synchronizer.SyncAllAsync(CancellationToken.None);

        _store.Chunks.Should().NotBeEmpty();
    }

    [Test]
    public void ExposeDocs_RequiresProjectName_AndASource()
    {
        var builder = new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build());

        var noProject = () => builder.ExposeDocs(x => x.FromDirectory("docs/wiki"));
        var noSource = () => builder.ExposeDocs(x => x.ProjectName = ProjectName);

        noProject.Should().Throw<InvalidOperationException>().WithMessage("*ProjectName*");
        noSource.Should().Throw<InvalidOperationException>().WithMessage("*FromDirectory or FromEmbeddedResources*");
    }

    [Test]
    public void ExposeDocs_MultipleCallsAllowed_ButNotTheSameSourceTwiceForAProject()
    {
        var services = new ServiceCollection();
        var builder = new BeaconBuilder(services, new ConfigurationBuilder().Build());

        builder.ExposeDocs(x =>
        {
            x.ProjectName = ProjectName;
            x.FromDirectory("docs/wiki");
        });
        builder.ExposeDocs(x =>
        {
            x.ProjectName = ProjectName;
            x.FromDirectory("docs/runbooks");
        });
        var duplicate = () => builder.ExposeDocs(x =>
        {
            x.ProjectName = ProjectName;
            x.FromDirectory("docs/wiki/");
        });

        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*docs:dir:docs/wiki*already registered*");
        services.Select(x => x.ImplementationInstance).OfType<HostDocsRegistration>().Should().HaveCount(2);
    }

    private Task<HostDocsSyncOutcome> SyncAsync()
    {
        return BuildSynchronizer(_indexer.Object).SyncAllAsync(CancellationToken.None)
            .ContinueWith(x => x.Result.Single(), TaskScheduler.Default);
    }

    private HostDocsSynchronizer BuildSynchronizer(IDocChunkIndexingService? indexer)
    {
        var options = new HostDocsOptions { ProjectName = ProjectName };
        options.FromDirectory("docs/wiki");

        var settings = SettingsProviderMock.Create();
        settings
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData { DocChunkWindowSentences = 5, DocChunkOverlapSentences = 1 });

        var factory = _store.Factory().Object;

        return new HostDocsSynchronizer(
            factory,
            new HostProjectResolver(factory, NullLogger<HostProjectResolver>.Instance),
            [new HostDocsRegistration(options)],
            settings.Object,
            indexer,
            _root,
            NullLogger<HostDocsSynchronizer>.Instance);
    }

    private void Write(string relativePath, string content)
    {
        File.WriteAllText(Path.Combine(_root, "docs", "wiki", relativePath.Replace('/', Path.DirectorySeparatorChar)), content);
    }
}
