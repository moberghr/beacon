# Execution Time Metrics - UI Implementation - January 2025

## Overview
Added execution time metrics and charts to both the Dashboard (Home page) and QueryDetails page. Users can now monitor query performance with visual metrics and historical trends.

## UI Changes

### 1. Home.razor (Dashboard Page)
**File**: src/Beacon.UI/Components/Pages/Home.razor

**Added Components**:
1. **Three Hero Cards** (lines 140-189):
   - Average Execution Time (blue - `hero-metric-card-primary`)
   - Fastest Query (green - `hero-metric-card-success`)
   - Slowest Query (red - `hero-metric-card-error`)

2. **Execution Time Chart** (lines 209-222):
   - Multi-series line chart showing Avg/Min/Max over last 30 days
   - Uses ChartHelper for consistent styling
   - 400px height, 45° X-axis rotation
   - Chart title: "Query Execution Performance - Last 30 Days"

**Code Changes**:
- Added `@using Beacon.UI.Helpers` (line 5)
- Added execution time chart fields (lines 464-468)
- Updated `LoadDashboardData()` to call `PrepareExecutionTimeChart()` (line 482)
- Added `PrepareExecutionTimeChart()` method (lines 608-638)
- Added `FormatExecutionTime()` helper method (lines 640-646)

**Data Source**: `DashboardStatisticsData` from `StatisticsService.GetDashboardStatistics()`
- `AvgExecutionTimeMs`, `MinExecutionTimeMs`, `MaxExecutionTimeMs`
- `ExecutionTimeHistory` (list of daily data points for last 30 days)

### 2. QueryDetails.razor (Query Details Page)
**File**: src/Beacon.UI/Components/Pages/Queries/QueryDetails.razor

**Added Components**:
1. **Three Hero Cards** (lines 122-162):
   - Average Execution Time (blue - `hero-metric-card-primary`)
   - Fastest Execution (green - `hero-metric-card-success`)
   - Slowest Execution (red - `hero-metric-card-error`)

2. **Execution Time Chart** (lines 311-327):
   - Multi-series line chart showing Avg/Min/Max over last 30 days
   - Uses ChartHelper for consistent styling
   - 400px height, 45° X-axis rotation
   - Chart title: "Query Execution Performance - Last 30 Days"
   - Only displayed if execution history exists

**Code Changes**:
- Added `@using Beacon.UI.Helpers` (line 10)
- Added execution time chart fields (lines 595-599)
- Updated `Load()` to call `PrepareExecutionTimeChart()` (lines 617-620)
- Added `PrepareExecutionTimeChart()` method (lines 880-910)
- Added `FormatExecutionTime()` helper method (lines 912-918)

**Data Source**: `QueryDetailsData` from `QueryService.GetQueryDetails()`
- `AvgExecutionTimeMs`, `MinExecutionTimeMs`, `MaxExecutionTimeMs`
- `ExecutionTimeHistory` (list of daily data points for last 30 days, query-specific)

## Visual Design

### Hero Cards
All execution time hero cards follow the same pattern:
- **Icon**: Material Design icon (Speed, FlashOn, HourglassTop)
- **Chip**: Label (Average, Fastest, Slowest) with white text on transparent background
- **Number**: Formatted execution time (large text)
- **Label**: Description text
- **Subtext**: Additional context (Home) or no subtext (QueryDetails)

### Chart Configuration
Using `ChartHelper.CreateMultiSeriesChartOptions()`:
- **Series Colors**: Blue (Average), Green (Minimum), Red/Orange (Maximum)
- **Height**: 400px
- **Width**: 100% (responsive)
- **Y-axis**: 5 ticks, grid lines enabled
- **X-axis**: Dates with 45° rotation, grid lines enabled
- **Line style**: Straight interpolation

## Execution Time Formatting

The `FormatExecutionTime()` method provides user-friendly time display:
- **< 1000ms**: Display as "X ms" (e.g., "523 ms")
- **>= 1000ms**: Display as "X.XX s" (e.g., "2.50 s")

Examples:
- 0ms → "0 ms"
- 523.45ms → "523 ms"
- 1234.56ms → "1.23 s"
- 15678.9ms → "15.68 s"

## Chart Data Structure

### ExecutionTimeDataPoint (from backend)
```csharp
public class ExecutionTimeDataPoint
{
    public DateTime Date { get; set; }
    public double AvgExecutionTimeMs { get; set; }
    public double MinExecutionTimeMs { get; set; }
    public double MaxExecutionTimeMs { get; set; }
}
```

### Chart Series
Three series for each chart:
1. **Average**: Blue line showing average execution time per day
2. **Minimum**: Green line showing fastest execution per day
3. **Maximum**: Red/orange line showing slowest execution per day

## Responsive Design

### Hero Cards Grid
**Home Page**:
- xs: 12 (full width)
- sm: 6 (2 columns)
- lg: 4 (3 columns)

**QueryDetails Page**:
- xs: 12 (full width)
- sm: 6 (2 columns)
- md: 4 (3 columns)

### Charts
- Full width (100%) on all screen sizes
- Fixed height (400px) for consistency
- X-axis labels rotated 45° to prevent overlap

## Testing Checklist

To verify the implementation:

1. **Dashboard (Home) Page**:
   - [ ] Three execution time hero cards display with correct colors
   - [ ] Execution times are formatted correctly (ms/s)
   - [ ] Chart displays with 3 series (Avg, Min, Max)
   - [ ] X-axis labels are rotated 45°
   - [ ] Chart shows data for last 30 days
   - [ ] Hero cards and chart only show when data is available

2. **Query Details Page**:
   - [ ] Three execution time hero cards display with correct colors
   - [ ] Execution times are formatted correctly (ms/s)
   - [ ] Chart displays with 3 series (Avg, Min, Max)
   - [ ] X-axis labels are rotated 45°
   - [ ] Chart shows query-specific data for last 30 days
   - [ ] Hero cards and chart only show when data is available
   - [ ] Chart only appears when execution history exists

## Build Status
✅ Full solution build succeeded with 0 warnings, 0 errors

## Files Modified

1. **src/Beacon.UI/Components/Pages/Home.razor**
   - Added using statement for ChartHelper
   - Added 3 execution time hero cards
   - Added execution time chart
   - Added chart fields and methods

2. **src/Beacon.UI/Components/Pages/Queries/QueryDetails.razor**
   - Added using statement for ChartHelper
   - Added 3 execution time hero cards
   - Added execution time chart
   - Added chart fields and methods

## Integration Points

### Backend Services Used
1. **StatisticsService.GetDashboardStatistics()**: Provides execution time statistics for all queries
2. **QueryService.GetQueryDetails()**: Provides execution time statistics per query

### Shared Components Used
1. **ChartHelper**: Provides consistent chart configuration
2. **MudBlazor Components**: MudPaper, MudChart, MudIcon, MudChip, MudText, etc.

### CSS Classes Used
- `hero-metric-card-primary`: Blue gradient for average time
- `hero-metric-card-success`: Green gradient for fastest time
- `hero-metric-card-error`: Red gradient for slowest time
- `hero-stat-number`: Large number styling
- `hero-stat-label`: Label text styling
- `section-title`: Section header styling
- `detail-card`: Card container styling

## Performance Considerations

1. **Data Loading**: Execution time data is fetched once on page load
2. **Chart Rendering**: Charts only render when data is available
3. **Memory**: Chart data is stored in component state, cleared on disposal
4. **Refresh**: Data refreshes when user clicks refresh button (Home) or navigates back to page

## Future Enhancements (Optional)

1. **Real-time Updates**: Add SignalR for live execution time updates
2. **Date Range Selector**: Allow users to choose custom date ranges
3. **Export**: Add button to export chart data to CSV
4. **Drill-down**: Click on chart to see detailed execution history
5. **Alerts**: Show warning indicators when execution times exceed thresholds
6. **Comparison**: Compare execution times across different queries
7. **Percentiles**: Add P50, P95, P99 metrics for better insights

## Notes

- Both pages use identical chart configuration for consistency
- Execution time data comes from `QueryExecutionHistory.ExecutionTimeMs` field
- No database migrations required - all data was already captured
- Charts use multi-series palette (blue, green, orange/red) from ChartHelper
- X-axis dates use short format (MM/dd) to save space
- Home page uses `ToShortDateWithoutYear()` extension method for consistency with existing charts
- QueryDetails page uses standard `ToString("MM/dd")` format
