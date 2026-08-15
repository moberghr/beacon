using Beacon.Core.Handlers.Metadata;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Metadata;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Integration.Api;

/// <summary>
/// Contract coverage (SC5/SC7) for the schema-relationship handlers behind
/// <c>/beacon/api/data-sources/{id}/relationships</c> and <c>/schema-health</c>.
/// </summary>
/// <remarks>
/// Handler-level rather than over HTTP: the two handlers whose behaviour is not a straight EF read take
/// only service dependencies, so they are testable without a database (§4.7 forbids
/// <c>UseInMemoryDatabase</c>). HTTP exposure itself is guaranteed by
/// <see cref="OpenApiContractTests.EveryMediatRHandlerIsExposedViaHttp"/>, and the EF-backed handlers'
/// query shapes are covered by <c>SchemaRelationshipTranslationTests</c>.
/// </remarks>
[TestFixture]
public class SchemaRelationshipEndpointTests
{
    [Test]
    public async Task GetSchemaHealth_ProjectsEveryFieldOfTheReport()
    {
        var report = new SchemaHealthReport(
            TableCount: 12,
            RelationshipCount: 9,
            VerifiedRelationshipCount: 7,
            UnverifiedRelationshipCount: 2,
            ComponentCount: 3,
            LargestComponentSize: 8,
            IsolatedTables: ["sales.audit_log"],
            JunctionTables: ["sales.order_products"]);

        var graphService = new Mock<ISchemaGraphService>();
        graphService
            .Setup(x => x.GetHealthAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

        var handler = new GetSchemaHealthHandler(graphService.Object);
        var result = await handler.Handle(new GetSchemaHealthQuery(7), CancellationToken.None);

        result.TableCount.Should().Be(12);
        result.RelationshipCount.Should().Be(9);
        result.VerifiedRelationshipCount.Should().Be(7);
        result.UnverifiedRelationshipCount.Should().Be(2);
        result.ComponentCount.Should().Be(3);
        result.LargestComponentSize.Should().Be(8);
        result.IsolatedTables.Should().BeEquivalentTo(["sales.audit_log"]);
        result.JunctionTables.Should().BeEquivalentTo(["sales.order_products"]);
    }

    [Test]
    public async Task GetSchemaHealth_InvalidDataSourceId_Throws()
    {
        var handler = new GetSchemaHealthHandler(Mock.Of<ISchemaGraphService>());

        var act = async () => await handler.Handle(new GetSchemaHealthQuery(0), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must be positive*");
    }

    [Test]
    public async Task PreviewDiscovery_ReturnsProposalsWithoutPersisting()
    {
        var proposals = new List<ProposedSchemaRelationship>
        {
            new("sales", "orders", "customer_id", "sales", "customers", "id", "customer",
                Core.Data.Enums.SchemaRelationshipOrigin.Inferred,
                Core.Data.Enums.SchemaRelationshipCardinality.OneToMany, null, 0.9)
        };

        var syncService = new Mock<ISchemaRelationshipSyncService>();
        syncService
            .Setup(x => x.PreviewAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(proposals);

        var handler = new PreviewSchemaRelationshipDiscoveryHandler(syncService.Object);
        var result = await handler.Handle(new PreviewSchemaRelationshipDiscoveryQuery(3), CancellationToken.None);

        result.Proposals.Should().ContainSingle();
        result.Proposals[0].SourceColumn.Should().Be("customer_id");
        result.Proposals[0].TargetTable.Should().Be("customers");
        result.Proposals[0].Confidence.Should().Be(0.9);

        // preview_discover proposes; it must never write.
        syncService.Verify(x => x.SyncAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task PreviewDiscovery_InvalidDataSourceId_Throws()
    {
        var handler = new PreviewSchemaRelationshipDiscoveryHandler(Mock.Of<ISchemaRelationshipSyncService>());

        var act = async () => await handler.Handle(
            new PreviewSchemaRelationshipDiscoveryQuery(-1), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task GetSchemaRelationships_InvalidDataSourceId_ThrowsBeforeTouchingTheDatabase()
    {
        var handler = new GetSchemaRelationshipsHandler(
            Mock.Of<Microsoft.EntityFrameworkCore.IDbContextFactory<Core.Data.BeaconContext>>());

        var act = async () => await handler.Handle(new GetSchemaRelationshipsQuery(0), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must be positive*");
    }
}
