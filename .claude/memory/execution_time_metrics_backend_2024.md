# Execution Time Metrics - Backend Implementation - December 2024

## Overview
Added execution time metrics and historical data to both the Dashboard (Home page) and QueryDetails page. This allows users to monitor query performance over time.

## Backend Changes

### 1. Extended QueryStatisticsModels.cs
**File**: Beacon.Core/Models/QueryExecutionHistory/QueryStatisticsModels.cs

**Added to DashboardStatisticsData**:
```csharp
// Execution Time Statistics
public double AvgExecutionTimeMs { get; set; }
public double MinExecutionTimeMs { get; set; }
public double MaxExecutionTimeMs { get; set; }
public List<ExecutionTimeDataPoint> ExecutionTimeHistory { get; set; } = new();
```

**New Model Class**:
```csharp
public class ExecutionTimeDataPoint
{
    public DateTime Date { get; set; }
    public double AvgExecutionTimeMs { get; set; }
    public double MinExecutionTimeMs { get; set; }
    public double MaxExecutionTimeMs { get; set; }
}
```

### 2. Updated StatisticsService
**File**: Beacon.Core/Services/StatisticsService.cs

**Added Calculations** (lines 89-112):
```csharp
// Get execution time statistics
var executionTimeStats = await context.QueryExecutionHistory
    .GroupBy(x => 1)
    .Select(g => new
    {
        AvgExecutionTimeMs = g.Average(h => h.ExecutionTimeMs),
        MinExecutionTimeMs = g.Min(h => h.ExecutionTimeMs),
        MaxExecutionTimeMs = g.Max(h => h.ExecutionTimeMs)
    })
    .FirstOrDefaultAsync(cancellationToken);

// Get execution time history (last 30 days, grouped by date)
var executionTimeHistory = await context.QueryExecutionHistory
    .Where(h => h.CreatedTime >= thirtyDaysAgo)
    .GroupBy(h => h.CreatedTime.Date)
    .Select(g => new ExecutionTimeDataPoint
    {
        Date = g.Key,
        AvgExecutionTimeMs = g.Average(h => h.ExecutionTimeMs),
        MinExecutionTimeMs = g.Min(h => h.ExecutionTimeMs),
        MaxExecutionTimeMs = g.Max(h => h.ExecutionTimeMs)
    })
    .OrderBy(x => x.Date)
    .ToListAsync(cancellationToken);
```

**Populated in Return** (lines 136-139):
```csharp
AvgExecutionTimeMs = executionTimeStats?.AvgExecutionTimeMs ?? 0,
MinExecutionTimeMs = executionTimeStats?.MinExecutionTimeMs ?? 0,
MaxExecutionTimeMs = executionTimeStats?.MaxExecutionTimeMs ?? 0,
ExecutionTimeHistory = executionTimeHistory
```

### 3. Extended QueryDetailsData
**File**: Beacon.Core/Services/QueryService.cs

**Added Using Statement** (line 17):
```csharp
using Beacon.Core.Models.QueryExecutionHistory;
```

**Added Properties to QueryDetailsData** (lines 85-92):
```csharp
// Execution Time Statistics
public double AvgExecutionTimeMs { get; set; }
public double MinExecutionTimeMs { get; set; }
public double MaxExecutionTimeMs { get; set; }
public List<ExecutionTimeDataPoint> ExecutionTimeHistory { get; set; } = new();
```

### 4. Updated QueryService.GetQueryDetails()
**File**: Beacon.Core/Services/QueryService.cs

**Added Calculations** (lines 380-409):
```csharp
// Get execution time statistics for this query (all subscriptions)
var executionTimeStats = await context.QueryExecutionHistory
    .Where(x => x.Subscription.QueryId == queryId)
    .GroupBy(x => 1)
    .Select(g => new
    {
        AvgExecutionTimeMs = g.Average(h => h.ExecutionTimeMs),
        MinExecutionTimeMs = g.Min(h => h.ExecutionTimeMs),
        MaxExecutionTimeMs = g.Max(h => h.ExecutionTimeMs)
    })
    .FirstOrDefaultAsync(cancellationToken);

// Get execution time history (last 30 days, grouped by date)
var executionTimeHistory = await context.QueryExecutionHistory
    .Where(x => x.Subscription.QueryId == queryId && x.CreatedTime >= cutoffDate)
    .GroupBy(x => x.CreatedTime.Date)
    .Select(g => new ExecutionTimeDataPoint
    {
        Date = g.Key,
        AvgExecutionTimeMs = g.Average(h => h.ExecutionTimeMs),
        MinExecutionTimeMs = g.Min(h => h.ExecutionTimeMs),
        MaxExecutionTimeMs = g.Max(h => h.ExecutionTimeMs)
    })
    .OrderBy(x => x.Date)
    .ToListAsync(cancellationToken);

result.AvgExecutionTimeMs = executionTimeStats?.AvgExecutionTimeMs ?? 0;
result.MinExecutionTimeMs = executionTimeStats?.MinExecutionTimeMs ?? 0;
result.MaxExecutionTimeMs = executionTimeStats?.MaxExecutionTimeMs ?? 0;
result.ExecutionTimeHistory = executionTimeHistory;
```

## Data Available

### Dashboard (Home Page)
From `DashboardStatisticsData`:
- `AvgExecutionTimeMs` - Average execution time across all queries
- `MinExecutionTimeMs` - Fastest query execution time
- `MaxExecutionTimeMs` - Slowest query execution time
- `ExecutionTimeHistory` - List of daily avg/min/max for last 30 days

### Query Details Page
From `QueryDetailsData`:
- `AvgExecutionTimeMs` - Average execution time for this specific query
- `MinExecutionTimeMs` - Fastest execution for this query
- `MaxExecutionTimeMs` - Slowest execution for this query
- `ExecutionTimeHistory` - List of daily avg/min/max for last 30 days (query-specific)

## Database Schema
No migration required! All data is already captured:
- `QueryExecutionHistory.ExecutionTimeMs` (double) - Already exists

## Build Status
✅ Backend build succeeded with 0 warnings, 0 errors

## Next Steps (UI Implementation)
1. Add execution time hero cards to Home.razor
2. Add execution time chart to Home.razor
3. Add execution time hero cards to QueryDetails.razor
4. Add execution time chart to QueryDetails.razor

## Metrics to Display

### Hero Cards
For both pages, display 3 hero cards:
- **Average Execution Time**: Show AvgExecutionTimeMs converted to ms/s
- **Fastest Query**: Show MinExecutionTimeMs
- **Slowest Query**: Show MaxExecutionTimeMs

### Chart
Display multi-series line chart showing:
- Series 1: Average execution time per day (blue)
- Series 2: Min execution time per day (green)
- Series 3: Max execution time per day (red/orange)
- X-axis: Dates (last 30 days)
- Y-axis: Execution time in milliseconds (with 45° rotation)
- Chart should use ChartHelper global configuration

## Format Helper Needed
Add a helper method to format execution times:
- < 1000ms: Show as "X ms"
- >= 1000ms: Show as "X.XX s"
- Example: 523ms → "523 ms", 2500ms → "2.50 s"
