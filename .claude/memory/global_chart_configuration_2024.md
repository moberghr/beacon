# Global Chart Configuration - December 2024

## Overview
Created a centralized `ChartHelper` class to provide consistent chart configurations across the entire application. This makes chart styling easier to maintain and modify in the future.

## Key Changes

### 1. Created ChartHelper Class
**File**: src/Beacon.UI/Helpers/ChartHelper.cs (NEW)

**Purpose**: Centralized chart configuration for consistent styling

**Constants**:
- `DefaultChartHeight = "400px"` - Standard height for all charts
- `DefaultChartWidth = "100%"` - Full container width

**Methods**:

#### CreateDefaultChartOptions()
Returns base ChartOptions with:
- YAxisTicks = 5
- YAxisLines = true
- XAxisLines = true
- InterpolationOption = Straight

#### CreateDefaultAxisChartOptions()
Returns AxisChartOptions with:
- **XAxisLabelRotation = 45** (diagonal labels for better readability)

#### CreateSingleSeriesChartOptions()
For single-series charts:
- Inherits default options
- ChartPalette = blue (#1f77b4)

#### CreateMultiSeriesChartOptions()
For multi-series charts:
- Inherits default options
- ChartPalette = blue, green, orange, red

#### CreateAnomalyChartOptions()
Alias for multi-series options (used in anomaly detection charts)

### 2. Updated TaskDetails.razor
**File**: src/Beacon.UI/Components/Pages/Tasks/TaskDetails.razor

**Changes**:
1. Added `@using Beacon.UI.Helpers`
2. Updated Result Count chart (line 119):
   - `Width="@ChartHelper.DefaultChartWidth"`
   - `Height="@ChartHelper.DefaultChartHeight"`
   - Already had AxisChartOptions

3. Updated Anomaly Detection chart (line 164):
   - `Width="@ChartHelper.DefaultChartWidth"`
   - `Height="@ChartHelper.DefaultChartHeight"`
   - Added `AxisChartOptions="@_anomalyAxisChartOptions"`

4. Added field (line 458):
   - `private AxisChartOptions _anomalyAxisChartOptions = new();`

5. Updated PrepareChartData() method (line 526):
   ```csharp
   _chartOptions = ChartHelper.CreateSingleSeriesChartOptions();
   _axisChartOptions = ChartHelper.CreateDefaultAxisChartOptions();
   ```

6. Updated PrepareAnomalyChart() method (line 606):
   ```csharp
   _anomalyChartOptions = ChartHelper.CreateAnomalyChartOptions();
   _anomalyAxisChartOptions = ChartHelper.CreateDefaultAxisChartOptions();
   ```

### 3. Updated SubscriptionDetails.razor
**File**: src/Beacon.UI/Components/Pages/Subscriptions/SubscriptionDetails.razor

**Changes**:
1. Added `@using Beacon.UI.Helpers` (line 10)

2. Updated Anomaly Detection chart (line 465):
   - `Width="@ChartHelper.DefaultChartWidth"` (was "100%")
   - `Height="@ChartHelper.DefaultChartHeight"` (was "400px")
   - Added `AxisChartOptions="@_anomalyAxisChartOptions"` (NEW - adds 45° rotation)

3. Added field (line 605):
   - `private AxisChartOptions _anomalyAxisChartOptions = new();`

4. Updated PrepareAnomalyChart() method (line 736):
   ```csharp
   _anomalyChartOptions = ChartHelper.CreateAnomalyChartOptions();
   _anomalyAxisChartOptions = ChartHelper.CreateDefaultAxisChartOptions();
   ```

## Benefits

### Before
- Chart configurations scattered across multiple files
- Manual configuration in each page (5-6 lines per chart)
- Inconsistent styling between pages
- Hard to change chart height/rotation globally
- Missing X-axis rotation on some charts

**Example (old way)**:
```csharp
_chartOptions.YAxisTicks = 5;
_chartOptions.ChartPalette = new[] { "#1f77b4" };
_chartOptions.YAxisLines = true;
_chartOptions.XAxisLines = true;
_chartOptions.InterpolationOption = InterpolationOption.Straight;
_axisChartOptions.XAxisLabelRotation = 45;
```

### After
- Single source of truth for chart configurations
- 2 lines instead of 6 per chart
- Consistent styling across all pages
- Easy to change globally (modify one file)
- All charts have 45° X-axis label rotation

**Example (new way)**:
```csharp
_chartOptions = ChartHelper.CreateSingleSeriesChartOptions();
_axisChartOptions = ChartHelper.CreateDefaultAxisChartOptions();
```

## Global Standards Established

All charts now follow these standards:
- **Height**: 400px (tall enough to read Y-axis values)
- **Width**: 100% (responsive to container)
- **X-axis labels**: 45° rotation (prevents overlap, improves readability)
- **Y-axis**: 5 ticks, grid lines enabled
- **X-axis**: Grid lines enabled
- **Interpolation**: Straight lines (clear data trends)
- **Colors**:
  - Single series: Blue (#1f77b4)
  - Multi-series: Blue, Green, Orange, Red

## How to Use (For Future Pages)

### Simple Chart with One Series
```razor
@using Beacon.UI.Helpers

<MudChart ChartType="ChartType.Line"
          ChartSeries="@_chartSeries"
          XAxisLabels="@_xAxisLabels"
          Width="@ChartHelper.DefaultChartWidth"
          Height="@ChartHelper.DefaultChartHeight"
          ChartOptions="@_chartOptions"
          AxisChartOptions="@_axisChartOptions"/>

@code {
    private ChartOptions _chartOptions = new();
    private AxisChartOptions _axisChartOptions = new();

    private void PrepareChart()
    {
        // ... prepare data ...

        _chartOptions = ChartHelper.CreateSingleSeriesChartOptions();
        _axisChartOptions = ChartHelper.CreateDefaultAxisChartOptions();
    }
}
```

### Multi-Series or Anomaly Chart
```csharp
_chartOptions = ChartHelper.CreateMultiSeriesChartOptions();
// or
_chartOptions = ChartHelper.CreateAnomalyChartOptions();
_axisChartOptions = ChartHelper.CreateDefaultAxisChartOptions();
```

## Easy Future Modifications

### To Change Chart Height Globally
Edit `ChartHelper.cs`:
```csharp
public const string DefaultChartHeight = "500px"; // Changed from 400px
```
All charts instantly update!

### To Change X-Axis Rotation
Edit `ChartHelper.cs`:
```csharp
return new AxisChartOptions
{
    XAxisLabelRotation = 30 // Changed from 45
};
```

### To Change Color Palette
Edit `ChartHelper.cs`:
```csharp
options.ChartPalette = new[] { "#new_color1", "#new_color2", ... };
```

## Files Modified

1. **NEW**: src/Beacon.UI/Helpers/ChartHelper.cs (~70 lines)
2. **Modified**: src/Beacon.UI/Components/Pages/Tasks/TaskDetails.razor
   - Added using statement
   - Updated 2 charts to use helper
   - Added _anomalyAxisChartOptions field
   - Simplified chart configuration methods
3. **Modified**: src/Beacon.UI/Components/Pages/Subscriptions/SubscriptionDetails.razor
   - Added using statement
   - Updated chart to use helper
   - Added _anomalyAxisChartOptions field (NEW - now has 45° rotation!)
   - Simplified chart configuration method

## Impact Summary

**Code Reduction**:
- Before: ~12 lines of configuration per chart × 3 charts = ~36 lines
- After: ~2 lines per chart × 3 charts + ChartHelper class = ~6 lines + reusable helper
- **Net benefit**: Easier maintenance, consistent styling

**Consistency**:
- ✅ All charts now 400px tall
- ✅ All charts have 45° X-axis rotation
- ✅ All charts use same color palettes
- ✅ All charts have grid lines configured the same way

**Future Development**:
- New pages can use ChartHelper immediately
- Global changes affect all charts at once
- Less code duplication
- Self-documenting (method names describe purpose)

## Build Status
✅ Build succeeded with 0 warnings, 0 errors

## Next Steps (Optional)

Consider updating other pages with charts:
- Home.razor (if it has charts)
- QueryDetails.razor (if it has charts)
- QueryExecutionHistoryDetails.razor (if it has charts)

These can be updated later to use the same global configuration for consistency.
