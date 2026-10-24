using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Services.Metadata;

namespace Beacon.Tests.Unit.Metadata;

/// <summary>
/// The catalog snapshot cache: one entry per project and data source, dropped for every project when the source's catalog
/// changes, never serving a load that overlapped an invalidation, expiring on its own and holding nothing over its size limit.
/// </summary>
[TestFixture]
public class CatalogSnapshotCacheTests
{
    private const int ProjectId = 3;
    private const int OtherProjectId = 4;
    private const int DataSourceId = 7;

    [Test]
    public async Task ASecondRead_IsServedFromTheCache()
    {
        using var cache = new CatalogSnapshotCache();
        var loads = 0;

        var first = await cache.GetOrLoadAsync(ProjectId, DataSourceId, _ => Load(ref loads, "v1"), _ => 1, CancellationToken.None);
        var second = await cache.GetOrLoadAsync(ProjectId, DataSourceId, _ => Load(ref loads, "v2"), _ => 1, CancellationToken.None);

        loads.Should().Be(1);
        second.Should().BeSameAs(first);
    }

    [Test]
    public async Task EachProject_HasItsOwnEntry_ForTheSameDataSource()
    {
        using var cache = new CatalogSnapshotCache();
        var loads = 0;

        var caller = await cache.GetOrLoadAsync(ProjectId, DataSourceId, _ => Load(ref loads, "caller"), _ => 1, CancellationToken.None);
        var other = await cache.GetOrLoadAsync(OtherProjectId, DataSourceId, _ => Load(ref loads, "other"), _ => 1, CancellationToken.None);

        loads.Should().Be(2, "one project's entry never serves another project");
        caller.Value.Should().Be("caller");
        other.Value.Should().Be("other");
    }

    [Test]
    public async Task Invalidate_DropsTheSourcesEntries_InEveryProject_AndNoOtherSource()
    {
        using var cache = new CatalogSnapshotCache();
        var loads = 0;
        await cache.GetOrLoadAsync(ProjectId, DataSourceId, _ => Load(ref loads, "a"), _ => 1, CancellationToken.None);
        await cache.GetOrLoadAsync(OtherProjectId, DataSourceId, _ => Load(ref loads, "b"), _ => 1, CancellationToken.None);
        await cache.GetOrLoadAsync(ProjectId, DataSourceId + 1, _ => Load(ref loads, "c"), _ => 1, CancellationToken.None);

        cache.Invalidate(DataSourceId);
        var caller = await cache.GetOrLoadAsync(ProjectId, DataSourceId, _ => Load(ref loads, "a2"), _ => 1, CancellationToken.None);
        var other = await cache.GetOrLoadAsync(OtherProjectId, DataSourceId, _ => Load(ref loads, "b2"), _ => 1, CancellationToken.None);
        var unrelated = await cache.GetOrLoadAsync(ProjectId, DataSourceId + 1, _ => Load(ref loads, "c2"), _ => 1, CancellationToken.None);

        caller.Value.Should().Be("a2");
        other.Value.Should().Be("b2");
        unrelated.Value.Should().Be("c", "another data source's snapshot is untouched");
        loads.Should().Be(5);
    }

    // A load that began before a write and finished after its invalidation read the catalog as it was: it is returned to
    // its own caller but never served to the next one.
    [Test]
    public async Task ALoadThatOverlappedAnInvalidation_IsNotServedAgain()
    {
        using var cache = new CatalogSnapshotCache();
        var loads = 0;

        await cache.GetOrLoadAsync(
            ProjectId,
            DataSourceId,
            _ =>
            {
                cache.Invalidate(DataSourceId);

                return Load(ref loads, "before the write");
            },
            _ => 1,
            CancellationToken.None);
        var next = await cache.GetOrLoadAsync(ProjectId, DataSourceId, _ => Load(ref loads, "after the write"), _ => 1, CancellationToken.None);

        next.Value.Should().Be("after the write");
    }

    [Test]
    public async Task AnEntry_ExpiresAfterItsLifetime()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        using var cache = new CatalogSnapshotCache(time);
        var loads = 0;
        await cache.GetOrLoadAsync(ProjectId, DataSourceId, _ => Load(ref loads, "v1"), _ => 1, CancellationToken.None);

        time.Advance(CatalogSnapshotCache.Expiration - TimeSpan.FromSeconds(1));
        var fresh = await cache.GetOrLoadAsync(ProjectId, DataSourceId, _ => Load(ref loads, "v2"), _ => 1, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2));
        var expired = await cache.GetOrLoadAsync(ProjectId, DataSourceId, _ => Load(ref loads, "v3"), _ => 1, CancellationToken.None);

        fresh.Value.Should().Be("v1");
        expired.Value.Should().Be("v3");
    }

    [Test]
    public async Task ASnapshotLargerThanTheEntryLimit_IsReturned_ButNotCached()
    {
        using var cache = new CatalogSnapshotCache();
        var loads = 0;

        var first = await cache.GetOrLoadAsync(ProjectId, DataSourceId, _ => Load(ref loads, "huge"), _ => CatalogSnapshotCache.MaxEntrySize + 1, CancellationToken.None);
        var second = await cache.GetOrLoadAsync(ProjectId, DataSourceId, _ => Load(ref loads, "huge again"), _ => CatalogSnapshotCache.MaxEntrySize + 1, CancellationToken.None);

        first.Value.Should().Be("huge");
        second.Value.Should().Be("huge again");
        loads.Should().Be(2);
    }

    // The cache is held to approximate bytes: a column's stored sample values count by their length.
    [Test]
    public void AStoredCatalog_IsSizedByItsTextIncludingStoredSamples()
    {
        var column = new SchemaColumn("status", "varchar", false, true, null, null, null);
        var bare = new StoredCatalog([new CatalogTable("sales", "orders", null, [column])]);
        var sampled = new StoredCatalog([new CatalogTable("sales", "orders", null, [column with { SampleValuesJson = new string('x', 10_000) }])]);

        var bareBytes = KnowledgeGraphService.ApproximateBytes(bare);
        var sampledBytes = KnowledgeGraphService.ApproximateBytes(sampled);

        bareBytes.Should().BeGreaterThan(0);
        (sampledBytes - bareBytes).Should().Be(20_000);
    }

    private static Task<Snapshot> Load(ref int loads, string value)
    {
        loads++;

        return Task.FromResult(new Snapshot(value));
    }

    private sealed record Snapshot(string Value);
}
