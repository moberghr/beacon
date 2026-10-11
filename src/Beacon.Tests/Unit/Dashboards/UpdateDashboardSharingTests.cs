using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Dashboards.UpdateDashboard;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Dashboards;

/// <summary>
/// An Edit grantee changes a dashboard's content, but sharing it with everyone or making it the default is the dashboard
/// Admin level's (its owner or an Admin grantee). Over in-memory sets (no database, §4.7).
/// </summary>
[TestFixture]
public class UpdateDashboardSharingTests
{
    private const int DashboardId = 4;
    private const string EditorId = "ext-editor";

    [Test]
    public async Task EditGrantee_ChangingContent_IsSaved()
    {
        var (handler, dashboard) = Handler(DashboardPermissionLevel.Edit);

        await handler.Handle(Command(isShared: false, isDefault: false, name: "renamed"), CancellationToken.None);

        dashboard.Name.Should().Be("renamed");
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task EditGrantee_SharingOrMakingDefault_IsForbidden(bool isShared, bool isDefault)
    {
        var (handler, dashboard) = Handler(DashboardPermissionLevel.Edit);

        var act = () => handler.Handle(Command(isShared, isDefault, name: "renamed"), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        dashboard.IsShared.Should().BeFalse();
        dashboard.IsDefault.Should().BeFalse();
        dashboard.Name.Should().Be("ops", "nothing is saved when the change is refused");
    }

    [Test]
    public async Task AdminLevel_SharingAndMakingDefault_IsSaved()
    {
        var (handler, dashboard) = Handler(DashboardPermissionLevel.Admin);

        await handler.Handle(Command(isShared: true, isDefault: true, name: "ops"), CancellationToken.None);

        dashboard.IsShared.Should().BeTrue();
        dashboard.IsDefault.Should().BeTrue();
    }

    private static UpdateDashboardCommand Command(bool isShared, bool isDefault, string name)
    {
        return new UpdateDashboardCommand(DashboardId, name, null, isShared, isDefault, null, null);
    }

    private static (UpdateDashboardHandler Handler, Dashboard Dashboard) Handler(DashboardPermissionLevel callerLevel)
    {
        var dashboard = new Dashboard { Id = DashboardId, Name = "ops", CreatedByUserId = "ext-owner" };
        var saved = new List<object>();
        var sets = new Dictionary<Type, object>
        {
            [typeof(Dashboard)] = RecordingBeaconContext.MemorySet([dashboard], saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));

        var dashboards = new Mock<IDashboardService>();
        dashboards
            .Setup(x => x.UserHasPermissionAsync(DashboardId, EditorId, It.IsAny<DashboardPermissionLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, string _, DashboardPermissionLevel required, CancellationToken _) => callerLevel >= required);

        var userContext = new Mock<IBeaconUserContext>();
        userContext.SetupGet(x => x.UserId).Returns(EditorId);

        return (new UpdateDashboardHandler(factory.Object, dashboards.Object, userContext.Object), dashboard);
    }
}
