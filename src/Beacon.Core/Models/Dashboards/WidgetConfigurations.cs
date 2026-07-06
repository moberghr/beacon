namespace Beacon.Core.Models.Dashboards;

public abstract class WidgetConfiguration
{
    public string DataSource { get; set; } = "Statistics"; // "Statistics", "Subscriptions", "Tasks", "Custom"
}

public class KpiCardWidgetConfiguration : WidgetConfiguration
{
    public string Metric { get; set; } = null!; // "TotalSubscriptions", "ActiveQueries", etc.
    public string? Icon { get; set; }
    public string? Badge { get; set; }
    public string? ColorClass { get; set; } // "hero-stat-card-success", etc.
}

public class ChartWidgetConfiguration : WidgetConfiguration
{
    public string ChartType { get; set; } = "Line"; // "Line", "Bar", "Pie"
    public List<string> Series { get; set; } = new(); // Metrics to display
    public string TimeRange { get; set; } = "30days"; // "7days", "30days", "90days"
}

public class TableWidgetConfiguration : WidgetConfiguration
{
    public string TableType { get; set; } = "TopSubscriptions"; // "TopSubscriptions", "RecentActivity", "PendingTasks"
    public int MaxRows { get; set; } = 10;
}

public class GaugeWidgetConfiguration : WidgetConfiguration
{
    public string Metric { get; set; } = null!;
    public double MinValue { get; set; }
    public double MaxValue { get; set; }
    public List<GaugeThreshold> Thresholds { get; set; } = new();
}

public class GaugeThreshold
{
    public double Value { get; set; }
    public string Color { get; set; } = null!;
    public string Label { get; set; } = null!;
}
