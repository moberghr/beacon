using Beacon.Core.Data;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class BeaconContextFactoryAdapterTests
{
    [Test]
    public async Task CreateDbContext_ServesTheProviderFactorysContext_ForSyncAndAsyncCreation()
    {
        await using var context = NpgsqlTestContext.Create();
        var factory = new Mock<IDbContextFactory<NpgsqlTestContext>>();
        factory
            .Setup(x => x.CreateDbContext())
            .Returns(context);
        IDbContextFactory<BeaconContext> adapter = new BeaconContextFactoryAdapter<NpgsqlTestContext>(factory.Object);

        adapter.CreateDbContext().Should().BeSameAs(context);
        (await adapter.CreateDbContextAsync(CancellationToken.None)).Should().BeSameAs(context);
    }
}
