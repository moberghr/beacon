using Beacon.Api.Authentication;
using Warp.UI.UIMiddleware;

namespace Beacon.SampleProject.Warp;

public static class WarpDashboardSetup
{
    /// <summary>
    /// Runs the host's configure delegate on the dashboard options, refuses the finished options when no authorization
    /// filter was set (Warp's default admits every request), and only then maps the dashboard.
    /// </summary>
    public static WarpUIOptions BuildGuardedOptions(Action<WarpUIOptions> configure)
    {
        var options = new WarpUIOptions();
        configure(options);
        WarpDashboardGuard.Require(options.Authorization, options.RoutePrefix);

        return options;
    }

    public static WebApplication UseGuardedWarpUI(this WebApplication app, Action<WarpUIOptions> configure)
    {
        app.UseWarpUI(BuildGuardedOptions(configure));

        return app;
    }
}
