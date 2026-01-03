using MudBlazor;

namespace Semantico.UI.Helpers;

/// <summary>
/// Provides default chart configurations for consistent styling across the application
/// </summary>
public static class ChartHelper
{
    /// <summary>
    /// Default chart height for all charts
    /// </summary>
    public const string DefaultChartHeight = "400px";

    /// <summary>
    /// Default chart width (full container width)
    /// </summary>
    public const string DefaultChartWidth = "100%";

    /// <summary>
    /// Creates default chart options with consistent styling
    /// </summary>
    public static ChartOptions CreateDefaultChartOptions()
    {
        return new ChartOptions
        {
            YAxisTicks = 5,
            YAxisLines = true,
            XAxisLines = true,
            InterpolationOption = InterpolationOption.Straight
        };
    }

    /// <summary>
    /// Creates default axis chart options with 45-degree X-axis label rotation
    /// </summary>
    public static AxisChartOptions CreateDefaultAxisChartOptions()
    {
        return new AxisChartOptions
        {
            XAxisLabelRotation = 45
        };
    }

    /// <summary>
    /// Creates chart options for single-series charts (blue palette)
    /// </summary>
    public static ChartOptions CreateSingleSeriesChartOptions()
    {
        var options = CreateDefaultChartOptions();
        options.ChartPalette = new[] { "#1f77b4" };
        return options;
    }

    /// <summary>
    /// Creates chart options for multi-series charts (blue, green, orange, red)
    /// </summary>
    public static ChartOptions CreateMultiSeriesChartOptions()
    {
        var options = CreateDefaultChartOptions();
        options.ChartPalette = new[] { "#1f77b4", "#2ca02c", "#ff7f0e", "#d62728" };
        return options;
    }

    /// <summary>
    /// Creates chart options for anomaly detection charts
    /// </summary>
    public static ChartOptions CreateAnomalyChartOptions()
    {
        return CreateMultiSeriesChartOptions();
    }
}
