using MudBlazor;
using Beacon.Core.Models.QueryExecutionHistory;

namespace Beacon.UI.Helpers;

/// <summary>
/// Provides default chart configurations for consistent styling across the application
/// </summary>
public static class ChartHelper
{
    public const string DefaultChartHeight = "400px";
    public const string DefaultChartWidth = "100%";

    public static LineChartOptions CreateDefaultLineChartOptions()
    {
        return new LineChartOptions
        {
            YAxisTicks = 5,
            YAxisLines = true,
            XAxisLines = true,
            XAxisLabelRotation = 45,
            InterpolationOption = InterpolationOption.Straight
        };
    }

    public static BarChartOptions CreateDefaultBarChartOptions()
    {
        return new BarChartOptions
        {
            YAxisTicks = 5,
            YAxisLines = true,
            XAxisLines = true,
            XAxisLabelRotation = 45
        };
    }

    public static ChartOptions CreateDefaultChartOptions()
    {
        return new ChartOptions();
    }

    public static LineChartOptions CreateSingleSeriesChartOptions()
    {
        var options = CreateDefaultLineChartOptions();
        options.ChartPalette = new[] { "#1f77b4" };
        return options;
    }

    public static LineChartOptions CreateMultiSeriesChartOptions()
    {
        var options = CreateDefaultLineChartOptions();
        options.ChartPalette = new[] { "#1f77b4", "#2ca02c", "#ff7f0e", "#d62728" };
        return options;
    }

    public static LineChartOptions CreateAnomalyChartOptions()
    {
        return CreateMultiSeriesChartOptions();
    }

    /// <summary>
    /// Builds execution time chart data (Average/Min/Max series) from a list of data points.
    /// </summary>
    public static (List<ChartSeries<double>> Series, string[] Labels, LineChartOptions Options)
        BuildExecutionTimeChart(IEnumerable<ExecutionTimeDataPoint> history, string dateFormat = "MM/dd")
    {
        var data = history.OrderBy(x => x.Date).ToList();

        var labels = data.Select(x => x.Date.ToString(dateFormat)).ToArray();

        var series = new List<ChartSeries<double>>
        {
            new() { Name = "Average", Data = data.Select(x => x.AvgExecutionTimeMs).ToArray() },
            new() { Name = "Minimum", Data = data.Select(x => x.MinExecutionTimeMs).ToArray() },
            new() { Name = "Maximum", Data = data.Select(x => x.MaxExecutionTimeMs).ToArray() }
        };

        return (series, labels, CreateMultiSeriesChartOptions());
    }
}
