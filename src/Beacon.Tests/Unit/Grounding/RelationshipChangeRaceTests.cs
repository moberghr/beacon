using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Metadata;
using Beacon.Core.Models;
using Beacon.Core.Services.Metadata;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Grounding;

/// <summary>
/// A relationship is changed, verified and removed only while it holds what was read: an edit writes its content with the
/// verification it leaves in one statement conditional on the label, cardinality and verification read; a removal is
/// conditional the same way; a verification names the label and cardinality the curator reviewed and is conditional on
/// them. A verification landing between an Editor's read and write refuses the Editor's change (409) and leaves nothing
/// verified with the Editor's content; a change landing between a curator's read and verification is not verified. Runs
/// the real handlers over in-memory rows (no database — §4.7); the concurrent request runs between the handler's read and
/// its write.
/// </summary>
[TestFixture]
public class RelationshipChangeRaceTests
{
    private const int DataSourceId = 10;
    private const int RelationshipId = 41;
    private const int CuratorUserId = 42;
    private const int EditorUserId = 8;

    private static readonly ProjectAccessScope EditorOfBoth = TestProjectAccess.Scope(
        new Dictionary<int, ProjectRole> { [1] = ProjectRole.Editor, [2] = ProjectRole.Editor },
        includesOpen: false,
        notOpen: []);

    [Test]
    public async Task AVerificationBetweenAnEditorsReadAndWrite_RefusesTheEdit_AndNothingIsVerifiedWithTheEditorsContent()
    {
        var row = Relationship(isVerified: false);
        var store = new Store(row, between: () => VerifyAsTheCurator(row));

        var act = () => UpdateHandler(store, EditorOfBoth, EditorUserId).Handle(Relabel("buyer"), CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconConflictException>()).Which.Message.Should().Be(UpdateSchemaRelationshipHandler.ChangedSinceLoadedMessage);
        row.Should().BeEquivalentTo(
            new { Label = "customer", Cardinality = SchemaRelationshipCardinality.OneToMany, IsVerified = true, VerifiedByUserId = (int?)CuratorUserId },
            x => x.ExcludingMissingMembers(),
            "the curator verified the content it saw; the Editor's change is not written");
        store.Graph.Verify(x => x.Invalidate(It.IsAny<int>()), Times.Never);
    }

    // A curator's change keeps a verified relationship verified: it must not re-verify one someone unverified meanwhile.
    [Test]
    public async Task AnUnverificationBetweenACuratorsReadAndWrite_RefusesTheCuratorsEdit()
    {
        var row = Relationship(isVerified: true);
        var store = new Store(row, between: () => Unverify(row));

        var act = () => UpdateHandler(store, TestProjectAccess.Unrestricted, CuratorUserId).Handle(Relabel("buyer"), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>();
        row.Should().BeEquivalentTo(
            new { Label = "customer", IsVerified = false, VerifiedByUserId = (int?)null },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task AnEditorsEdit_WithNothingInBetween_IsWrittenUnverified()
    {
        var row = Relationship(isVerified: false);
        var store = new Store(row);

        var result = await UpdateHandler(store, EditorOfBoth, EditorUserId).Handle(Relabel("buyer") with { Cardinality = SchemaRelationshipCardinality.OneToOne }, CancellationToken.None);

        result.IsVerified.Should().BeFalse();
        row.Should().BeEquivalentTo(
            new { Label = "buyer", Cardinality = SchemaRelationshipCardinality.OneToOne, IsVerified = false, VerifiedByUserId = (int?)null, VerifiedTime = (DateTime?)null },
            x => x.ExcludingMissingMembers());
        store.Graph.Verify(x => x.Invalidate(DataSourceId), Times.Once);
    }

    // The route lets an Editor remove an unverified relationship; one a curator verifies between the read and the removal is
    // a curator's to remove, so it stays.
    [Test]
    public async Task AVerificationBetweenAnEditorsReadAndRemoval_RefusesTheRemoval()
    {
        var row = Relationship(isVerified: false);
        var store = new Store(row, between: () => VerifyAsTheCurator(row));

        var act = () => DeleteHandler(store, EditorOfBoth).Handle(new DeleteSchemaRelationshipCommand(DataSourceId, RelationshipId), CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconConflictException>()).Which.Message.Should().Be(UpdateSchemaRelationshipHandler.ChangedSinceLoadedMessage);
        row.ArchivedTime.Should().BeNull();
        row.IsVerified.Should().BeTrue();
    }

    [Test]
    public async Task AChangeBetweenARemovalsReadAndWrite_RefusesTheRemoval()
    {
        var row = Relationship(isVerified: false);
        var store = new Store(row, between: () => row.Label = "buyer");

        var act = () => DeleteHandler(store, EditorOfBoth).Handle(new DeleteSchemaRelationshipCommand(DataSourceId, RelationshipId), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>();
        row.ArchivedTime.Should().BeNull();
    }

    [Test]
    public async Task ARemoval_WithNothingInBetween_ArchivesTheRelationship()
    {
        var row = Relationship(isVerified: false);
        var store = new Store(row);

        await DeleteHandler(store, EditorOfBoth).Handle(new DeleteSchemaRelationshipCommand(DataSourceId, RelationshipId), CancellationToken.None);

        row.ArchivedTime.Should().NotBeNull();
        store.Graph.Verify(x => x.Invalidate(DataSourceId), Times.Once);
    }

    // An Editor's change lands between the curator's read and the verification: the curator reviewed other content.
    [Test]
    public async Task AnEditBetweenACuratorsReadAndVerification_IsNotVerified()
    {
        var row = Relationship(isVerified: false);
        var store = new Store(row, between: () => row.Label = "buyer");

        var act = () => VerifyHandler(store).Handle(Verify("customer", SchemaRelationshipCardinality.OneToMany), CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconConflictException>()).Which.Message.Should().Be(UpdateSchemaRelationshipHandler.ChangedSinceLoadedMessage);
        row.Should().BeEquivalentTo(new { Label = "buyer", IsVerified = false, VerifiedByUserId = (int?)null }, x => x.ExcludingMissingMembers());
    }

    // The relationship changed after the curator's list was loaded: the verification names what the curator saw, and the
    // relationship holds something else — a label, or only a cardinality, another.
    [TestCase("buyer", SchemaRelationshipCardinality.OneToMany)]
    [TestCase("customer", SchemaRelationshipCardinality.OneToOne)]
    [TestCase("Customer", SchemaRelationshipCardinality.OneToMany)]
    public async Task AVerificationOfContentTheRelationshipNoLongerHolds_IsRefused(string label, SchemaRelationshipCardinality cardinality)
    {
        var row = Relationship(isVerified: false);
        row.Label = label;
        row.Cardinality = cardinality;
        var store = new Store(row);

        var act = () => VerifyHandler(store).Handle(Verify("customer", SchemaRelationshipCardinality.OneToMany), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>();
        row.IsVerified.Should().BeFalse();
        store.Lookups.Should().Be(1, "nothing is written after the read shows other content");
    }

    [Test]
    public async Task AVerificationOfTheContentReviewed_RecordsTheCurator()
    {
        var row = Relationship(isVerified: false);
        var store = new Store(row);

        await VerifyHandler(store).Handle(Verify("customer", SchemaRelationshipCardinality.OneToMany), CancellationToken.None);

        row.Should().BeEquivalentTo(new { IsVerified = true, VerifiedByUserId = (int?)CuratorUserId }, x => x.ExcludingMissingMembers());
        row.VerifiedTime.Should().NotBeNull();
    }

    // Verifying names what was reviewed; unverifying needs nothing reviewed.
    [Test]
    public async Task VerifyingWithoutTheReviewedContent_IsRefused_WhileUnverifyingNeedsNone()
    {
        var unverified = Relationship(isVerified: false);
        var verified = Relationship(isVerified: true);

        var withoutLabel = () => VerifyHandler(new Store(unverified)).Handle(new VerifySchemaRelationshipCommand(DataSourceId, RelationshipId, true) { ReviewedCardinality = SchemaRelationshipCardinality.OneToMany }, CancellationToken.None);
        var withoutCardinality = () => VerifyHandler(new Store(unverified)).Handle(new VerifySchemaRelationshipCommand(DataSourceId, RelationshipId, true) { ReviewedLabel = "customer" }, CancellationToken.None);
        await VerifyHandler(new Store(verified)).Handle(new VerifySchemaRelationshipCommand(DataSourceId, RelationshipId, false), CancellationToken.None);

        (await withoutLabel.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(VerifySchemaRelationshipHandler.ReviewedContentRequiredMessage);
        await withoutCardinality.Should().ThrowAsync<InvalidOperationException>();
        unverified.IsVerified.Should().BeFalse();
        verified.Should().BeEquivalentTo(new { IsVerified = false, VerifiedByUserId = (int?)null, VerifiedTime = (DateTime?)null }, x => x.ExcludingMissingMembers());
    }

    private static void VerifyAsTheCurator(SchemaRelationship row)
    {
        row.IsVerified = true;
        row.VerifiedByUserId = CuratorUserId;
        row.VerifiedTime = DateTime.UtcNow;
    }

    private static void Unverify(SchemaRelationship row)
    {
        row.IsVerified = false;
        row.VerifiedByUserId = null;
        row.VerifiedTime = null;
    }

    private static UpdateSchemaRelationshipCommand Relabel(string label)
    {
        return new UpdateSchemaRelationshipCommand
        {
            DataSourceId = DataSourceId,
            Id = RelationshipId,
            Label = label,
            Cardinality = SchemaRelationshipCardinality.OneToMany
        };
    }

    private static VerifySchemaRelationshipCommand Verify(string label, SchemaRelationshipCardinality cardinality)
    {
        return new VerifySchemaRelationshipCommand(DataSourceId, RelationshipId, true)
        {
            ReviewedLabel = label,
            ReviewedCardinality = cardinality
        };
    }

    private static UpdateSchemaRelationshipHandler UpdateHandler(Store store, ProjectAccessScope scope, int beaconUserId)
    {
        return new UpdateSchemaRelationshipHandler(
            store.Factory,
            store.Graph.Object,
            TestProjectAccess.Evaluator(scope),
            TestPermissions.None,
            TestProjectAccess.Actors(beaconUserId: beaconUserId));
    }

    private static DeleteSchemaRelationshipHandler DeleteHandler(Store store, ProjectAccessScope scope)
    {
        return new DeleteSchemaRelationshipHandler(
            store.Factory,
            store.Graph.Object,
            TestProjectAccess.Evaluator(scope),
            TestPermissions.None,
            TestProjectAccess.Actors(beaconUserId: EditorUserId),
            NullLogger<DeleteSchemaRelationshipHandler>.Instance);
    }

    private static VerifySchemaRelationshipHandler VerifyHandler(Store store)
    {
        return new VerifySchemaRelationshipHandler(
            store.Factory,
            store.Graph.Object,
            TestProjectAccess.Actors(beaconUserId: CuratorUserId),
            NullLogger<VerifySchemaRelationshipHandler>.Instance);
    }

    private static SchemaRelationship Relationship(bool isVerified)
    {
        return new SchemaRelationship
        {
            Id = RelationshipId,
            DataSourceId = DataSourceId,
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

    /// <summary>
    /// The relationship, served to the handler's context; <c>between</c> runs when the handler reaches for the relationships
    /// a second time — its write, after its read — as a concurrent request would between the two. Only the relationships
    /// are served: a handler that reads anything else fails at once.
    /// </summary>
    private sealed class Store
    {
        public Store(SchemaRelationship row, Action? between = null)
        {
            var saved = new List<object>();
            var relationships = RecordingBeaconContext.MemorySet(new List<SchemaRelationship> { row }, saved);
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(new RacingSets(relationships, () => Lookups++, between), saved));
            Factory = factory.Object;
        }

        public IDbContextFactory<BeaconContext> Factory { get; }

        public Mock<ISchemaGraphService> Graph { get; } = new();

        /// <summary>How many times the handler reached for the relationships.</summary>
        public int Lookups { get; private set; }
    }

    // The relationships set, with a hook on the second time it is reached for.
    private sealed class RacingSets(object relationships, Func<int> reached, Action? between) : IReadOnlyDictionary<Type, object>
    {
        public int Count => 1;

        public IEnumerable<Type> Keys => [typeof(SchemaRelationship)];

        public IEnumerable<object> Values => [relationships];

        public object this[Type key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();

        public bool ContainsKey(Type key)
        {
            return key == typeof(SchemaRelationship);
        }

        public bool TryGetValue(Type key, [MaybeNullWhen(false)] out object value)
        {
            if (key != typeof(SchemaRelationship))
            {
                value = null;

                return false;
            }

            if (reached() == 1)
            {
                between?.Invoke();
            }

            value = relationships;

            return true;
        }

        public IEnumerator<KeyValuePair<Type, object>> GetEnumerator()
        {
            yield return new KeyValuePair<Type, object>(typeof(SchemaRelationship), relationships);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
