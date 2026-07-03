# Cross-Project QueryStep Architecture: Unified Multi-Database Query Execution

## Overview
This document outlines the implementation of a revolutionary query execution system where **queries can span multiple databases and projects**. By moving ProjectId from Query to QueryStep, each step in a query chain can execute against different databases, enabling powerful cross-database analytics and data integration scenarios.

## Core Architectural Principle

### Cross-Project QueryStep Model
- **ProjectId lives in QueryStep** (not Query) - each step can target a different database
- **All queries are collections of QuerySteps** (minimum 1, no maximum)
- **Single queries** = 1 QueryStep (StepOrder = 1)
- **Multi-step queries** = Multiple QuerySteps (StepOrder = 1, 2, 3...)
- **Cross-project queries** = QuerySteps with different ProjectIds
- **One execution engine** handles all scenarios: single-DB, multi-step, and cross-DB

### Revolutionary Capabilities Enabled
- **Cross-Database Analytics**: Join PostgreSQL user data with SQL Server transaction data
- **Multi-Environment Queries**: Compare production vs staging databases
- **Data Integration**: Combine data from multiple business systems in one query
- **Federated Reporting**: Create reports spanning different database engines
- **Migration Validation**: Compare old system vs new system data
- **Hybrid Cloud Queries**: Query on-premises and cloud databases together

### Benefits of Cross-Project Architecture
- **Maximum flexibility** - each step targets the optimal database
- **Data federation** - combine data from heterogeneous sources
- **Zero data movement** - query data where it lives
- **Unified interface** - one tool for all database interactions
- **Scalable architecture** - add new databases without system changes

## Data Model Architecture

### Query Entity (Project-Agnostic)
```csharp
public class Query : ArchivableBaseEntity
{
    // Core properties - no ProjectId here anymore!
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    
    // Navigation properties
    public List<Subscription> Subscriptions { get; set; } = new();
    public List<QueryStep> Steps { get; set; } = new();  // Always has ≥1 step
    
    // Computed properties for powerful query analysis
    public bool IsMultiStep => Steps.Count > 1;
    public bool IsCrossProject => Steps.Select(s => s.ProjectId).Distinct().Count() > 1;
    public bool IsCrossDatabase => Steps.Select(s => s.Project.DatabaseEngineType).Distinct().Count() > 1;
    public List<int> ProjectIds => Steps.Select(s => s.ProjectId).Distinct().ToList();
    public List<DatabaseEngineType> DatabaseEngines => Steps.Select(s => s.Project.DatabaseEngineType).Distinct().ToList();
    
    // Backward compatibility properties (map to first step)
    public string SqlValue => Steps.OrderBy(s => s.StepOrder).FirstOrDefault()?.SqlValue ?? "";
    public int ProjectId => Steps.OrderBy(s => s.StepOrder).FirstOrDefault()?.ProjectId ?? 0;
    public Project? Project => Steps.OrderBy(s => s.StepOrder).FirstOrDefault()?.Project;
    public List<QueryParameter> Parameters => ConvertStepParametersToQueryParameters();
}
```

### QueryStep Entity (Project Owner)
```csharp
public class QueryStep : BaseEntity
{
    public required int QueryId { get; set; }
    public Query Query { get; set; } = null!;
    
    // ===== PROJECT OWNERSHIP - KEY ARCHITECTURAL CHANGE =====
    public required int ProjectId { get; set; }  // Each step owns its database target
    public Project Project { get; set; } = null!;  // Each step can be different DB engine!
    
    public required int StepOrder { get; set; }  // 1, 2, 3...
    public required string SqlValue { get; set; }  // SQL executed against this step's database
    public string? Name { get; set; }
    public string? Description { get; set; }
    public List<QueryStepParameter> Parameters { get; set; } = new();
}
```

### QueryStepParameter Entity (Step-Specific Parameters)
```csharp
public class QueryStepParameter : BaseEntity
{
    public required int QueryStepId { get; set; }
    public QueryStep QueryStep { get; set; } = null!;
    public required string Name { get; set; }
    public required ParameterType Type { get; set; }
    public string? Description { get; set; }
    public string? Placeholder { get; set; }
}
```

### Enhanced Data Transfer Models
```csharp
// QueryData with cross-project support
public class QueryData
{
    public int QueryId { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public DateTime CreatedTime { get; set; }
    public int SubscriptionsCount { get; set; }
    public List<QueryStepData> Steps { get; set; } = new();
    
    // Cross-project computed properties
    public bool IsMultiStep => Steps.Count > 1;
    public bool IsCrossProject => Steps.Select(s => s.ProjectId).Distinct().Count() > 1;
    public bool IsCrossDatabase => Steps.Select(s => s.DatabaseEngineType).Distinct().Count() > 1;
    public List<string> ProjectNames => Steps.Select(s => s.ProjectName).Distinct().ToList();
    public List<DatabaseEngineType> DatabaseEngines => Steps.Select(s => s.DatabaseEngineType).Distinct().ToList();
    
    // Backward compatibility properties (map to first step)
    public string SqlValue
    {
        get => Steps.OrderBy(s => s.StepOrder).FirstOrDefault()?.SqlValue ?? "";
        set
        {
            EnsureSingleStep();
            Steps[0].SqlValue = value;
        }
    }
    public int ProjectId
    {
        get => Steps.OrderBy(s => s.StepOrder).FirstOrDefault()?.ProjectId ?? 0;
        set
        {
            EnsureSingleStep();
            Steps[0].ProjectId = value;
        }
    }
    public string? ProjectName => Steps.OrderBy(s => s.StepOrder).FirstOrDefault()?.ProjectName;
    public List<QueryParameterData> Parameters
    {
        get => Steps.OrderBy(s => s.StepOrder).FirstOrDefault()?.Parameters.Select(ConvertToQueryParameterData).ToList() ?? new();
        set
        {
            EnsureSingleStep();
            Steps[0].Parameters = value.Select(ConvertToQueryStepParameterData).ToList();
        }
    }
}

// Enhanced QueryStepData with project information
public class QueryStepData
{
    public int StepId { get; set; }
    public int StepOrder { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string SqlValue { get; set; } = null!;
    
    // Project information for this step
    public int ProjectId { get; set; }
    public string ProjectName { get; set; } = null!;
    public DatabaseEngineType DatabaseEngineType { get; set; }
    public string DatabaseEngineDescription => DatabaseEngineType.ToString();
    
    public List<QueryStepParameterData> Parameters { get; set; } = new();
}

// Enhanced execution result models
public class QueryExecutionResult
{
    public List<QueryStepResult> StepResults { get; set; } = new();
    public QueryResult? FinalResult { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public double TotalExecutionTimeMs { get; set; }
    
    // Cross-project analysis
    public bool IsMultiStep { get; set; }
    public bool IsCrossProject { get; set; }
    public bool IsCrossDatabase { get; set; }
    public List<string> ProjectsInvolved { get; set; } = new();
    public List<DatabaseEngineType> DatabaseEnginesUsed { get; set; } = new();
    public Dictionary<string, double> ExecutionTimeByProject { get; set; } = new();
}

public class QueryStepResult
{
    public int StepOrder { get; set; }
    public string StepName { get; set; } = null!;
    public string SqlQuery { get; set; } = null!;
    
    // Project context for this step
    public string ProjectName { get; set; } = null!;
    public string DatabaseEngine { get; set; } = null!;
    public DatabaseEngineType DatabaseEngineType { get; set; }
    
    public List<IDictionary<string, object?>> PreviewResults { get; set; } = new();
    public List<IDictionary<string, object?>> AllResults { get; set; } = new();
    public int TotalRows { get; set; }
    public double ExecutionTimeMs { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
```

## Service Implementation (Cross-Database Execution)

### Enhanced QueryService Interface
```csharp
public interface IQueryService
{
    // ===== EXISTING METHODS (unchanged signatures for backward compatibility) =====
    Task<BaseResponse> CreateQuery(QueryData queryData, CancellationToken cancellationToken);
    Task<BaseResponse> UpdateQuery(QueryData queryData, CancellationToken cancellationToken);
    Task DeleteQuery(int queryId, CancellationToken cancellationToken);
    Task<PagedList<QueryData>> GetQueries(GetQueriesRequest request, CancellationToken cancellationToken);
    Task<QueryDetailsData> GetQueryDetails(int queryId, CancellationToken cancellationToken);
    Task<QueryResult> ExecuteQuery(int subscriptionId, CancellationToken cancellationToken);
    
    // ===== ENHANCED METHODS FOR CROSS-PROJECT FUNCTIONALITY =====
    Task<QueryExecutionResult> ExecuteQueryAdvanced(int queryId, string? finalQuery = null, List<ParameterValue>? parameters = null, int? finalQueryProjectId = null, CancellationToken cancellationToken = default);
    Task<QueryStepResult> PreviewQueryStep(int queryId, int stepOrder, CancellationToken cancellationToken);
    
    // Step management with project context
    Task<BaseResponse> AddQueryStep(int queryId, QueryStepData stepData, CancellationToken cancellationToken);
    Task<BaseResponse> UpdateQueryStep(int queryId, int stepOrder, QueryStepData stepData, CancellationToken cancellationToken);
    Task DeleteQueryStep(int queryId, int stepOrder, CancellationToken cancellationToken);
    
    // Cross-project analysis
    Task<List<ProjectData>> GetAvailableProjects(CancellationToken cancellationToken);
    Task<CrossProjectAnalysisResult> AnalyzeQueryComplexity(int queryId, CancellationToken cancellationToken);
}
```

### Cross-Database Execution Engine
```csharp
public async Task<QueryExecutionResult> ExecuteQueryAdvanced(int queryId, string? finalQuery = null, List<ParameterValue>? parameters = null, int? finalQueryProjectId = null, CancellationToken cancellationToken = default)
{
    var query = await GetQueryWithSteps(queryId, cancellationToken);
    
    // All queries use the same execution path - handles single-DB, multi-step, and cross-DB!
    return await ExecuteQuerySteps(query, finalQuery, parameters, finalQueryProjectId, cancellationToken);
}

private async Task<QueryExecutionResult> ExecuteQuerySteps(Query query, string? finalQuery, List<ParameterValue>? parameters, int? finalQueryProjectId, CancellationToken cancellationToken)
{
    var stepResults = new List<QueryStepResult>();
    var virtualTableManager = new VirtualTableManager(_logger);
    var totalExecutionTime = 0.0;
    var projectExecutionTimes = new Dictionary<string, double>();
    
    _logger.LogInformation("Executing query chain {QueryId}: {StepCount} steps across {ProjectCount} projects", 
        query.Id, query.Steps.Count, query.ProjectIds.Count);
    
    // Execute each step against its own database
    foreach (var step in query.Steps.OrderBy(s => s.StepOrder))
    {
        _logger.LogDebug("Executing step {StepOrder} against project {ProjectName} ({DatabaseEngine})", 
            step.StepOrder, step.Project.Name, step.Project.DatabaseEngineType);
        
        var stepResult = await ExecuteStep(step, parameters);
        stepResults.Add(stepResult);
        totalExecutionTime += stepResult.ExecutionTimeMs;
        
        // Track execution time by project
        var projectKey = $"{step.Project.Name} ({step.Project.DatabaseEngineType})";
        projectExecutionTimes[projectKey] = projectExecutionTimes.GetValueOrDefault(projectKey, 0) + stepResult.ExecutionTimeMs;
        
        if (stepResult.Success)
        {
            // Add results to virtual table manager with project context
            var projectInfo = new ProjectInfo
            {
                Name = step.Project.Name,
                DatabaseEngine = step.Project.DatabaseEngineType.ToString(),
                DatabaseEngineType = step.Project.DatabaseEngineType
            };
            
            virtualTableManager.AddVirtualTable($"@result{step.StepOrder}", stepResult.AllResults, projectInfo);
        }
        else
        {
            _logger.LogError("Step {StepOrder} failed: {ErrorMessage}", step.StepOrder, stepResult.ErrorMessage);
            break; // Stop execution on first failure
        }
    }
    
    QueryResult? finalResult = null;
    bool allStepsSucceeded = stepResults.All(s => s.Success);
    
    if (!string.IsNullOrEmpty(finalQuery) && allStepsSucceeded)
    {
        // Final query execution - choose target database
        var targetProject = finalQueryProjectId.HasValue 
            ? await GetProject(finalQueryProjectId.Value, cancellationToken)
            : query.Steps.OrderBy(s => s.StepOrder).First().Project; // Default to first step's project
        
        _logger.LogInformation("Executing final query against project {ProjectName} ({DatabaseEngine})", 
            targetProject.Name, targetProject.DatabaseEngineType);
        
        finalResult = await ExecuteFinalQuery(finalQuery, targetProject, virtualTableManager);
        totalExecutionTime += finalResult.ExecutionTimeMs;
        
        projectExecutionTimes[$"{targetProject.Name} ({targetProject.DatabaseEngineType})"] = 
            projectExecutionTimes.GetValueOrDefault($"{targetProject.Name} ({targetProject.DatabaseEngineType})", 0) + finalResult.ExecutionTimeMs;
    }
    else if (query.Steps.Count == 1 && string.IsNullOrEmpty(finalQuery) && allStepsSucceeded)
    {
        // Single-step query - convert step result to QueryResult
        finalResult = ConvertStepToQueryResult(stepResults[0], query);
    }
    
    return new QueryExecutionResult
    {
        StepResults = stepResults,
        FinalResult = finalResult,
        Success = allStepsSucceeded,
        TotalExecutionTimeMs = totalExecutionTime,
        IsMultiStep = query.IsMultiStep,
        IsCrossProject = query.IsCrossProject,
        IsCrossDatabase = query.IsCrossDatabase,
        ProjectsInvolved = stepResults.Select(s => s.ProjectName).Distinct().ToList(),
        DatabaseEnginesUsed = stepResults.Select(s => s.DatabaseEngineType).Distinct().ToList(),
        ExecutionTimeByProject = projectExecutionTimes
    };
}

private async Task<QueryStepResult> ExecuteStep(QueryStep step, List<ParameterValue>? parameters)
{
    var stepParameters = ExtractStepParameters(step, parameters);
    var compiledSql = QueryHelper.CompileSql(step.SqlValue, stepParameters);
    
    // Each step executes against its own project/database
    var (results, executionTimeMs, timedOut) = await ExecuteQueryAsync(
        step.Project.DatabaseEngineType,   // Each step can be different engine type!
        step.Project.ConnectionString,     // Each step connects to different database
        compiledSql,
        null // Use default timeout
    );
    
    return new QueryStepResult
    {
        StepOrder = step.StepOrder,
        StepName = step.Name ?? $"Step {step.StepOrder}",
        SqlQuery = compiledSql,
        ProjectName = step.Project.Name,
        DatabaseEngine = step.Project.DatabaseEngineType.ToString(),
        DatabaseEngineType = step.Project.DatabaseEngineType,
        PreviewResults = results.Take(10).ToList(),
        AllResults = results,
        TotalRows = results.Count,
        ExecutionTimeMs = executionTimeMs,
        Success = !timedOut,
        ErrorMessage = timedOut ? "Step execution timed out" : null
    };
}

private async Task<QueryResult> ExecuteFinalQuery(string finalQuery, Project targetProject, VirtualTableManager virtualTableManager)
{
    // Parse final query and replace virtual table references with actual data
    var parsedSql = virtualTableManager.ParseAndReplaceVirtualTables(finalQuery, targetProject.DatabaseEngineType);
    
    var (results, executionTimeMs, timedOut) = await ExecuteQueryAsync(
        targetProject.DatabaseEngineType,
        targetProject.ConnectionString,
        parsedSql,
        null
    );
    
    return new QueryResult
    {
        QueryResults = JsonSerializer.Serialize(results.Take(20)),
        TotalRecords = results.Count,
        ProjectName = targetProject.Name,
        SqlQuery = finalQuery, // Show original query with virtual table references
        AllRecords = results,
        TopRecords = results.Take(20).ToList(),
        SubscriptionName = "Cross-Project Query Chain",
        ExecutionTimeMs = executionTimeMs,
        TimedOut = timedOut,
        Recipients = new List<RecipientData>()
    };
}
```

## Enhanced UI Components (Cross-Project Interface)

### QueryStepBuilder.razor (Per-Step Project Selection)
```razor
<MudContainer>
    @if (IsCrossProject || IsCrossDatabase)
    {
        <MudAlert Severity="Severity.Info" Dense="true" Class="mb-4">
            <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2">
                <MudIcon Icon="Icons.Material.Filled.CompareArrows" />
                <div>
                    <MudText Typo="Typo.body1"><strong>Cross-Project Query Chain</strong></MudText>
                    <MudText Typo="Typo.caption">
                        This query spans @UniqueProjects.Count projects and @UniqueDatabaseEngines.Count database engine types.
                        Virtual tables will be created in memory to join data from different sources.
                    </MudText>
                </div>
            </MudStack>
        </MudAlert>
    }
    
    @foreach (var step in Steps.OrderBy(s => s.StepOrder))
    {
        <MudCard Class="mb-3" Style="@GetStepCardStyle(step)">
            <MudCardHeader>
                <CardHeaderContent>
                    <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2" Justify="Justify.SpaceBetween">
                        <MudTextField @bind-Value="step.Name" 
                                     Label="Step Name" 
                                     Variant="Variant.Text" 
                                     Margin="Margin.Dense"
                                     Style="max-width: 200px;" />
                        
                        <MudSelect @bind-Value="step.ProjectId" 
                                  Label="Database" 
                                  Variant="Variant.Outlined"
                                  Margin="Margin.Dense"
                                  Style="min-width: 200px;"
                                  OnSelectionChanged="@((int projectId) => OnProjectChanged(step, projectId))">
                            @foreach (var project in Projects)
                            {
                                <MudSelectItem Value="project.Id">
                                    <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="1">
                                        <MudIcon Icon="@GetDatabaseIcon(project.DatabaseEngineType)" 
                                                Size="Size.Small" 
                                                Color="@GetDatabaseColor(project.DatabaseEngineType)" />
                                        <MudText>@project.Name</MudText>
                                        <MudChip Size="Size.Small" Color="Color.Default">
                                            @project.DatabaseEngineType.ToString()
                                        </MudChip>
                                    </MudStack>
                                </MudSelectItem>
                            }
                        </MudSelect>
                        
                        @if (IsCrossProject && step.ProjectId != Steps.FirstOrDefault()?.ProjectId)
                        {
                            <MudChip Size="Size.Small" Color="Color.Warning" Icon="Icons.Material.Filled.SwapHoriz">
                                Cross-DB
                            </MudChip>
                        }
                    </MudStack>
                </CardHeaderContent>
                <CardHeaderActions>
                    <MudIconButton Icon="Icons.Material.Filled.PlayArrow" 
                                  Title="Preview Step" 
                                  Color="Color.Primary"
                                  OnClick="@(() => PreviewStep(step))" />
                    <MudIconButton Icon="Icons.Material.Filled.ArrowUpward" 
                                  Title="Move Up" 
                                  OnClick="@(() => MoveStepUp(step))"
                                  Disabled="@(step.StepOrder == 1)" />
                    <MudIconButton Icon="Icons.Material.Filled.ArrowDownward" 
                                  Title="Move Down" 
                                  OnClick="@(() => MoveStepDown(step))"
                                  Disabled="@(step.StepOrder == Steps.Count)" />
                    <MudIconButton Icon="Icons.Material.Filled.Delete" 
                                  Title="Delete Step" 
                                  Color="Color.Error"
                                  OnClick="@(() => RemoveStep(step))"
                                  Disabled="@(Steps.Count == 1)" />
                </CardHeaderActions>
            </MudCardHeader>
            <MudCardContent>
                <MudTextField @bind-Value="step.SqlValue" 
                             Label="SQL Query" 
                             Multiline="true" 
                             Lines="6" 
                             Required="true"
                             HelperText="@GetProjectHelperText(step)"
                             Class="mb-3" />
                
                @if (step.StepOrder > 1)
                {
                    <MudAlert Severity="Severity.Info" Dense="true" Class="mb-3">
                        <MudStack Spacing="1">
                            <MudText Typo="Typo.body2">
                                <MudIcon Icon="Icons.Material.Filled.TableChart" Size="Size.Small" />
                                <strong>Available virtual tables:</strong> @string.Join(", ", GetAvailableVirtualTables(step.StepOrder))
                            </MudText>
                            @if (HasCrossProjectVirtualTables(step.StepOrder))
                            {
                                <MudText Typo="Typo.caption" Color="Color.Warning">
                                    <MudIcon Icon="Icons.Material.Filled.Warning" Size="Size.Small" />
                                    Some virtual tables contain data from different database engines. 
                                    Data types will be normalized automatically.
                                </MudText>
                            }
                        </MudStack>
                    </MudAlert>
                }
                
                <MudText Typo="Typo.h6" Class="mt-3">Parameters</MudText>
                <StepParameterEditor @bind-Parameters="step.Parameters" />
                
                @if (!string.IsNullOrEmpty(step.Description))
                {
                    <MudTextField @bind-Value="step.Description" 
                                 Label="Description (optional)" 
                                 Multiline="true" 
                                 Lines="2" 
                                 Class="mt-2" />
                }
            </MudCardContent>
        </MudCard>
    }
    
    <MudStack Row="true" Justify="Justify.SpaceBetween" AlignItems="AlignItems.Center" Class="mt-4">
        <MudButton StartIcon="Icons.Material.Filled.Add" 
                  OnClick="AddNewStep" 
                  Color="Color.Primary"
                  Variant="Variant.Filled">
            Add Step
        </MudButton>
        
        @if (Steps.Count > 1)
        {
            <MudStack AlignItems="AlignItems.End">
                <MudText Typo="Typo.caption"><strong>Query Chain Summary:</strong></MudText>
                <MudChipSet>
                    @foreach (var step in Steps.OrderBy(s => s.StepOrder))
                    {
                        <MudChip Size="Size.Small" 
                                Color="@GetProjectChipColor(step.ProjectId)"
                                Icon="@GetDatabaseIcon(GetProjectById(step.ProjectId)?.DatabaseEngineType)">
                            @($"Step {step.StepOrder}: {GetProjectById(step.ProjectId)?.Name}")
                        </MudChip>
                    }
                </MudChipSet>
                
                @if (IsCrossProject)
                {
                    <MudText Typo="Typo.caption" Color="Color.Info">
                        <MudIcon Icon="Icons.Material.Filled.Storage" Size="Size.Small" />
                        @UniqueProjects.Count projects • @UniqueDatabaseEngines.Count database engines
                    </MudText>
                }
            </MudStack>
        }
    </MudStack>
</MudContainer>

@code
{
    [Parameter] public List<QueryStepData> Steps { get; set; } = new();
    [Parameter] public EventCallback<List<QueryStepData>> StepsChanged { get; set; }
    [Parameter] public List<ProjectData> Projects { get; set; } = new();
    
    // Cross-project analysis properties
    private bool IsCrossProject => Steps.Select(s => s.ProjectId).Distinct().Count() > 1;
    private bool IsCrossDatabase => Steps.Select(s => s.DatabaseEngineType).Distinct().Count() > 1;
    private List<string> UniqueProjects => Steps.Select(s => GetProjectById(s.ProjectId)?.Name ?? "Unknown").Distinct().ToList();
    private List<DatabaseEngineType> UniqueDatabaseEngines => Steps.Select(s => s.DatabaseEngineType).Distinct().ToList();
    
    private string GetStepCardStyle(QueryStepData step)
    {
        var project = GetProjectById(step.ProjectId);
        if (project == null) return "";
        
        var borderColor = project.DatabaseEngineType switch
        {
            DatabaseEngineType.PostgreSQL => "#336791",
            DatabaseEngineType.MSSQL => "#CC2927",
            DatabaseEngineType.MySQL => "#4479A1",
            _ => "#757575"
        };
        
        return $"border-left: 4px solid {borderColor};";
    }
    
    private string GetProjectHelperText(QueryStepData step)
    {
        var project = GetProjectById(step.ProjectId);
        if (project == null) return "Select a database project";
        
        var baseText = $"Executing on {project.Name} ({project.DatabaseEngineType})";
        
        if (IsCrossProject && step.StepOrder > 1)
        {
            baseText += " • Virtual tables from previous steps will be available in memory";
        }
        
        return baseText;
    }
    
    private string GetDatabaseIcon(DatabaseEngineType? engineType) => engineType switch
    {
        DatabaseEngineType.PostgreSQL => Icons.Material.Filled.Storage,
        DatabaseEngineType.MSSQL => Icons.Material.Filled.Storage,
        DatabaseEngineType.MySQL => Icons.Material.Filled.Storage,
        _ => Icons.Material.Filled.Storage
    };
    
    private Color GetDatabaseColor(DatabaseEngineType? engineType) => engineType switch
    {
        DatabaseEngineType.PostgreSQL => Color.Primary,
        DatabaseEngineType.MSSQL => Color.Error,
        DatabaseEngineType.MySQL => Color.Info,
        _ => Color.Default
    };
    
    private Color GetProjectChipColor(int projectId)
    {
        var colors = new[] { Color.Primary, Color.Secondary, Color.Tertiary, Color.Info, Color.Success, Color.Warning };
        return colors[projectId % colors.Length];
    }
    
    private bool HasCrossProjectVirtualTables(int currentStepOrder)
    {
        var previousSteps = Steps.Where(s => s.StepOrder < currentStepOrder);
        return previousSteps.Select(s => s.DatabaseEngineType).Distinct().Count() > 1;
    }
    
    private List<string> GetAvailableVirtualTables(int currentStepOrder)
    {
        return Steps.Where(s => s.StepOrder < currentStepOrder)
            .OrderBy(s => s.StepOrder)
            .Select(s => $"@result{s.StepOrder}")
            .ToList();
    }
    
    private ProjectData? GetProjectById(int projectId) => Projects.FirstOrDefault(p => p.Id == projectId);
    
    private async Task OnProjectChanged(QueryStepData step, int newProjectId)
    {
        var project = GetProjectById(newProjectId);
        if (project != null)
        {
            step.ProjectName = project.Name;
            step.DatabaseEngineType = project.DatabaseEngineType;
        }
        await StepsChanged.InvokeAsync(Steps);
    }
    
    private async Task AddNewStep()
    {
        var newStepOrder = Steps.Count > 0 ? Steps.Max(s => s.StepOrder) + 1 : 1;
        var defaultProject = Projects.FirstOrDefault() ?? new ProjectData();
        
        Steps.Add(new QueryStepData
        {
            StepOrder = newStepOrder,
            Name = $"Step {newStepOrder}",
            SqlValue = "",
            ProjectId = defaultProject.Id,
            ProjectName = defaultProject.Name,
            DatabaseEngineType = defaultProject.DatabaseEngineType,
            Parameters = new List<QueryStepParameterData>()
        });
        
        await StepsChanged.InvokeAsync(Steps);
    }
}
```

### Enhanced QueryDetails.razor (Cross-Project Display)
```razor
@page "/beacon/queries/details/{id:int}"

<MudContainer Class="my-4 px-4"  MaxWidth="MaxWidth.ExtraExtraLarge">
    <BeaconPageHeader Icon="@GetQueryIcon()" 
                        Title="@GetQueryTitle()" 
                        ButtonText="Add subscription" 
                        OnClick="@AddSubscription"/>
    
    @if (Model?.IsCrossProject == true)
    {
        <MudAlert Severity="Severity.Info" Class="mb-4">
            <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2">
                <MudIcon Icon="Icons.Material.Filled.CompareArrows" Size="Size.Large" />
                <div>
                    <MudText Typo="Typo.h6">Cross-Project Query Chain</MudText>
                    <MudText>
                        This query spans <strong>@Model.ProjectNames.Count projects</strong> 
                        and <strong>@Model.DatabaseEngines.Count database engine types</strong>.
                    </MudText>
                    <MudChipSet Class="mt-2">
                        @foreach (var engine in Model.DatabaseEngines)
                        {
                            <MudChip Size="Size.Small" Icon="@GetDatabaseIcon(engine)" Color="@GetDatabaseColor(engine)">
                                @engine.ToString()
                            </MudChip>
                        }
                    </MudChipSet>
                </div>
            </MudStack>
        </MudAlert>
    }
    
    <MudCard Class="my-4 px-2">
        <MudCardContent>
            @if (_loading)
            {
                <MudSkeleton Animation="Animation.Wave"/>
            }
            else
            {
                <MudGrid>
                    <MudItem xs="12" sm="6">
                        <MudStack Spacing="2">
                            <MudText><strong>Query ID:</strong> @Model?.Id</MudText>
                            <MudText><strong>Type:</strong> @GetQueryTypeDescription()</MudText>
                            <MudText><strong>Steps:</strong> @Model?.Steps.Count</MudText>
                            <MudText><strong>Created:</strong> @Model?.CreatedTime</MudText>
                        </MudStack>
                    </MudItem>
                    <MudItem xs="12" sm="6">
                        <MudStack Spacing="2">
                            <MudText><strong>Projects:</strong> @string.Join(", ", Model?.ProjectNames ?? new List<string>())</MudText>
                            <MudText><strong>Total Executions:</strong> @Model?.TotalExecutions</MudText>
                            <MudText><strong>Notifications Sent:</strong> @Model?.SentNotifications</MudText>
                        </MudStack>
                    </MudItem>
                </MudGrid>
                
                @if (Model?.Steps.Count > 1)
                {
                    @* ===== MULTI-STEP DISPLAY ===== *@
                    <MudTabs Class="mt-4">
                        <MudTabPanel Text="Query Steps" Icon="Icons.Material.Filled.List">
                            @foreach (var step in Model.Steps.OrderBy(s => s.StepOrder))
                            {
                                <MudCard Class="mb-3" Style="@GetStepCardStyle(step)">
                                    <MudCardHeader>
                                        <CardHeaderContent>
                                            <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2">
                                                <MudText Typo="Typo.h6">@step.Name</MudText>
                                                <MudChip Size="Size.Small" 
                                                        Icon="@GetDatabaseIcon(step.DatabaseEngineType)" 
                                                        Color="@GetDatabaseColor(step.DatabaseEngineType)">
                                                    @step.ProjectName
                                                </MudChip>
                                            </MudStack>
                                        </CardHeaderContent>
                                        <CardHeaderActions>
                                            <MudIconButton Icon="Icons.Material.Filled.PlayArrow" 
                                                          Title="Preview Step"
                                                          Color="Color.Primary"
                                                          OnClick="@(() => PreviewStep(step.StepOrder))" />
                                        </CardHeaderActions>
                                    </MudCardHeader>
                                    <MudCardContent>
                                        <MudText Typo="Typo.caption" Color="Color.Secondary" Class="mb-2">
                                            Executes on @step.ProjectName (@step.DatabaseEngineType)
                                        </MudText>
                                        <CodeHighlight Code="@step.SqlValue" />
                                        @if (step.Parameters.Any())
                                        {
                                            <MudText Class="mt-2"><strong>Parameters:</strong> @step.Parameters.ToJson()</MudText>
                                        }
                                    </MudCardContent>
                                </MudCard>
                            }
                        </MudTabPanel>
                        
                        <MudTabPanel Text="Final Query Builder" Icon="Icons.Material.Filled.JoinFull">
                            <ChainedQueryExecutor QueryId="Model.Id" 
                                                 VirtualTables="@GetVirtualTableNames()" 
                                                 AvailableProjects="@GetInvolvedProjects()" />
                        </MudTabPanel>
                        
                        <MudTabPanel Text="Execution Analysis" Icon="Icons.Material.Filled.Analytics">
                            <QueryExecutionAnalysis QueryId="Model.Id" />
                        </MudTabPanel>
                    </MudTabs>
                }
                else
                {
                    @* ===== SINGLE-STEP DISPLAY (enhanced with project info) ===== *@
                    <MudStack Class="mt-4" Spacing="3">
                        <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2">
                            <MudText Typo="Typo.h6">SQL Query</MudText>
                            <MudChip Size="Size.Small" 
                                    Icon="@GetDatabaseIcon(Model.Steps.First().DatabaseEngineType)" 
                                    Color="@GetDatabaseColor(Model.Steps.First().DatabaseEngineType)">
                                @Model.Steps.First().ProjectName
                            </MudChip>
                        </MudStack>
                        
                        <CodeHighlight Code="@Model?.Steps.FirstOrDefault()?.SqlValue" />
                        
                        @if (Model?.Steps.FirstOrDefault()?.Parameters.Any() == true)
                        {
                            <MudText><strong>Parameters:</strong> @Model.Steps.First().Parameters.ToJson()</MudText>
                        }
                        
                        <MudButton StartIcon="Icons.Material.Filled.Add" 
                                  OnClick="ConvertToMultiStep"
                                  Color="Color.Primary"
                                  Variant="Variant.Outlined">
                            Convert to Multi-Step Chain
                        </MudButton>
                    </MudStack>
                }
            }
        </MudCardContent>
    </MudCard>
    
    @* ===== SUBSCRIPTIONS TABLE (unchanged) ===== *@
    <MudCard Class="my-4 px-2">
        <MudCardContent>
            <MudText Typo="Typo.h6">Subscriptions</MudText>
            <MudTable T="SubscriptionListData" Items="@Model?.Subscriptions" Hover="true" OnRowClick="OnRowClick">
                @* Existing subscription table structure *@
            </MudTable>
        </MudCardContent>
    </MudCard>
</MudContainer>

@code
{
    [Parameter] public int Id { get; set; }
    public QueryDetailsData? Model;
    private bool _loading;
    
    private string GetQueryIcon() => Model?.IsCrossProject == true 
        ? Icons.Material.Filled.CompareArrows 
        : Icons.Material.Filled.QueryStats;
    
    private string GetQueryTitle() => Model?.IsCrossProject == true
        ? $"Cross-Project Query: {Model?.Name}"
        : $"Query: {Model?.Name}";
    
    private string GetQueryTypeDescription()
    {
        if (Model == null) return "Loading...";
        
        var parts = new List<string>();
        
        if (Model.IsMultiStep)
            parts.Add("Multi-step");
        else
            parts.Add("Single query");
            
        if (Model.IsCrossProject)
            parts.Add("Cross-project");
            
        if (Model.IsCrossDatabase)
            parts.Add("Cross-database");
        
        return string.Join(" • ", parts);
    }
    
    private List<ProjectData> GetInvolvedProjects()
    {
        return Model?.Steps.Select(s => new ProjectData 
        { 
            Id = s.ProjectId, 
            Name = s.ProjectName, 
            DatabaseEngineType = s.DatabaseEngineType 
        }).DistinctBy(p => p.Id).ToList() ?? new List<ProjectData>();
    }
    
    // Other helper methods...
}
```

## Enhanced VirtualTableManager (Cross-Database Support)

```csharp
public class VirtualTableManager : IDisposable
{
    private readonly Dictionary<string, List<IDictionary<string, object?>>> _virtualTables = new();
    private readonly Dictionary<string, ProjectInfo> _tableProjectInfo = new();
    private readonly ILogger<VirtualTableManager> _logger;
    
    public VirtualTableManager(ILogger<VirtualTableManager> logger)
    {
        _logger = logger;
    }
    
    public void AddVirtualTable(string name, List<IDictionary<string, object?>> data, ProjectInfo projectInfo)
    {
        _virtualTables[name] = data;
        _tableProjectInfo[name] = projectInfo;
        
        _logger.LogDebug("Added virtual table {VirtualTableName} with {RowCount} rows from project {ProjectName} ({DatabaseEngine})", 
            name, data.Count, projectInfo.Name, projectInfo.DatabaseEngine);
    }
    
    public string ParseAndReplaceVirtualTables(string sql, DatabaseEngineType targetDatabaseEngine)
    {
        var virtualTablePattern = new Regex(@"@result(\d+)", RegexOptions.IgnoreCase);
        var matches = virtualTablePattern.Matches(sql);
        
        if (!matches.Any())
        {
            _logger.LogDebug("No virtual table references found in SQL");
            return sql;
        }
        
        var referencedTables = matches.Cast<Match>()
            .Select(m => m.Value.ToLower())
            .Distinct()
            .ToList();
        
        _logger.LogDebug("Found virtual table references: {VirtualTables}", string.Join(", ", referencedTables));
        _logger.LogDebug("Target database engine: {TargetEngine}", targetDatabaseEngine);
        
        // Log cross-database scenario
        var sourceDatabases = referencedTables
            .Where(t => _tableProjectInfo.ContainsKey(t))
            .Select(t => _tableProjectInfo[t].DatabaseEngine)
            .Distinct()
            .ToList();
            
        if (sourceDatabases.Count > 1 || sourceDatabases.Any(db => db != targetDatabaseEngine.ToString()))
        {
            _logger.LogInformation("Cross-database query detected: combining data from {SourceEngines} into {TargetEngine}",
                string.Join(", ", sourceDatabases), targetDatabaseEngine);
        }
        
        // Validate all referenced tables exist
        foreach (var tableName in referencedTables)
        {
            if (!_virtualTables.ContainsKey(tableName))
            {
                throw new BeaconException($"Virtual table {tableName} is referenced but not available. Available: {string.Join(", ", _virtualTables.Keys)}");
            }
        }
        
        return targetDatabaseEngine switch
        {
            DatabaseEngineType.PostgreSQL => BuildPostgreSqlWithCtes(sql, referencedTables),
            DatabaseEngineType.MSSQL => BuildSqlServerWithCtes(sql, referencedTables),
            DatabaseEngineType.MySQL => BuildMySqlWithCtes(sql, referencedTables),
            _ => throw new BeaconException($"Virtual table support not implemented for {targetDatabaseEngine}")
        };
    }
    
    private string BuildSqlServerWithCtes(string sql, List<string> referencedTables)
    {
        var cteBuilder = new StringBuilder("WITH ");
        var cteList = new List<string>();
        
        foreach (var tableName in referencedTables)
        {
            var data = _virtualTables[tableName];
            var projectInfo = _tableProjectInfo[tableName];
            var cteName = tableName.Substring(1); // Remove @
            
            var cte = BuildSqlServerCte(cteName, data, projectInfo);
            cteList.Add(cte);
        }
        
        cteBuilder.Append(string.Join(",\n", cteList));
        cteBuilder.AppendLine();
        cteBuilder.Append(sql);
        
        return cteBuilder.ToString();
    }
    
    private string BuildSqlServerCte(string cteName, List<IDictionary<string, object?>> data, ProjectInfo sourceProject)
    {
        if (!data.Any())
        {
            return $@"{cteName} AS (
    -- Empty result set from {sourceProject.Name} ({sourceProject.DatabaseEngine})
    SELECT NULL as EmptyTable WHERE 1=0
)";
        }
        
        var columns = data.First().Keys.ToList();
        var valueRows = data.Take(1000).Select(row => // Limit to 1000 rows for performance
            "(" + string.Join(", ", columns.Select(col => FormatValueForSqlServer(row[col]))) + ")"
        );
        
        return $@"{cteName} AS (
    -- Data from {sourceProject.Name} ({sourceProject.DatabaseEngine}) - {data.Count} rows
    SELECT {string.Join(", ", columns.Select(c => $"[{c}]"))}
    FROM (VALUES {string.Join(",\n           ", valueRows)}) AS t({string.Join(", ", columns.Select(c => $"[{c}]"))})
)";
    }
    
    // Enhanced formatting methods that handle cross-database data type differences
    private string FormatValueForSqlServer(object? value)
    {
        return value switch
        {
            null => "NULL",
            string s => $"N'{s.Replace("'", "''")}'",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.fff}'",
            DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss.fff zzz}'",
            bool b => b ? "CAST(1 AS BIT)" : "CAST(0 AS BIT)",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double dbl => dbl.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(),
            long l => l.ToString(),
            Guid g => $"'{g}'",
            byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
            _ => $"N'{value.ToString()?.Replace("'", "''")}'"
        };
    }
    
    private string FormatValueForPostgreSql(object? value)
    {
        return value switch
        {
            null => "NULL",
            string s => $"'{s.Replace("'", "''")}'",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'::timestamp",
            DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss zzz}'::timestamptz",
            bool b => b ? "true" : "false",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double dbl => dbl.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(),
            long l => l.ToString(),
            Guid g => $"'{g}'::uuid",
            byte[] bytes => $"'\\x{Convert.ToHexString(bytes)}'::bytea",
            _ => $"'{value.ToString()?.Replace("'", "''")}'"
        };
    }
    
    private string FormatValueForMySql(object? value)
    {
        return value switch
        {
            null => "NULL",
            string s => $"'{s.Replace("'", "\\'").Replace("\\", "\\\\")}'",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
            bool b => b ? "1" : "0",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double dbl => dbl.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(),
            long l => l.ToString(),
            Guid g => $"'{g}'",
            byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
            _ => $"'{value.ToString()?.Replace("'", "\\'").Replace("\\", "\\\\")}'"
        };
    }
    
    public CrossDatabaseAnalysisResult AnalyzeCrossDatabase()
    {
        var analysis = new CrossDatabaseAnalysisResult();
        
        foreach (var kvp in _tableProjectInfo)
        {
            var tableName = kvp.Key;
            var projectInfo = kvp.Value;
            var rowCount = _virtualTables[tableName].Count;
            
            analysis.TableAnalysis[tableName] = new VirtualTableAnalysis
            {
                TableName = tableName,
                SourceProject = projectInfo.Name,
                SourceDatabaseEngine = projectInfo.DatabaseEngine,
                RowCount = rowCount,
                ColumnCount = _virtualTables[tableName].FirstOrDefault()?.Count ?? 0,
                EstimatedMemoryUsageMB = EstimateMemoryUsage(_virtualTables[tableName])
            };
        }
        
        analysis.TotalTables = _virtualTables.Count;
        analysis.UniqueDatabaseEngines = _tableProjectInfo.Values.Select(p => p.DatabaseEngine).Distinct().ToList();
        analysis.TotalRowsInMemory = _virtualTables.Values.Sum(v => v.Count);
        analysis.EstimatedTotalMemoryUsageMB = analysis.TableAnalysis.Values.Sum(t => t.EstimatedMemoryUsageMB);
        
        return analysis;
    }
    
    private double EstimateMemoryUsage(List<IDictionary<string, object?>> data)
    {
        if (!data.Any()) return 0;
        
        // Rough estimation: average 50 bytes per value
        var totalValues = data.Sum(row => row.Count);
        return (totalValues * 50) / (1024.0 * 1024.0); // Convert to MB
    }
    
    public void ClearVirtualTables()
    {
        _virtualTables.Clear();
        _tableProjectInfo.Clear();
        _logger.LogDebug("Cleared all virtual tables and project info");
    }
    
    public void Dispose()
    {
        ClearVirtualTables();
        GC.SuppressFinalize(this);
    }
}

public class ProjectInfo
{
    public required string Name { get; init; }
    public required string DatabaseEngine { get; init; }
    public required DatabaseEngineType DatabaseEngineType { get; init; }
}

public class CrossDatabaseAnalysisResult
{
    public Dictionary<string, VirtualTableAnalysis> TableAnalysis { get; set; } = new();
    public int TotalTables { get; set; }
    public List<string> UniqueDatabaseEngines { get; set; } = new();
    public int TotalRowsInMemory { get; set; }
    public double EstimatedTotalMemoryUsageMB { get; set; }
}

public class VirtualTableAnalysis
{
    public string TableName { get; set; } = null!;
    public string SourceProject { get; set; } = null!;
    public string SourceDatabaseEngine { get; set; } = null!;
    public int RowCount { get; set; }
    public int ColumnCount { get; set; }
    public double EstimatedMemoryUsageMB { get; set; }
}
```

## Database Migration Strategy (ProjectId Relocation)

### Complete Migration Script
```sql
-- ===== PHASE 1: CREATE NEW TABLES WITH PROJECTID IN QUERYSTEPS =====

CREATE TABLE QuerySteps (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    QueryId INT NOT NULL,
    ProjectId INT NOT NULL,  -- *** MOVED HERE from Queries table ***
    StepOrder INT NOT NULL,
    Name NVARCHAR(255) NULL,
    Description NVARCHAR(MAX) NULL,
    SqlValue NVARCHAR(MAX) NOT NULL,
    CreatedTime DATETIME2 NOT NULL DEFAULT GETDATE(),
    ModifiedTime DATETIME2 NOT NULL DEFAULT GETDATE(),
    CONSTRAINT FK_QuerySteps_QueryId FOREIGN KEY (QueryId) REFERENCES Queries(Id) ON DELETE CASCADE,
    CONSTRAINT FK_QuerySteps_ProjectId FOREIGN KEY (ProjectId) REFERENCES Projects(Id),
    CONSTRAINT UQ_QuerySteps_QueryId_StepOrder UNIQUE (QueryId, StepOrder)
);

CREATE TABLE QueryStepParameters (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    QueryStepId INT NOT NULL,
    Name NVARCHAR(100) NOT NULL,
    Type INT NOT NULL,
    Description NVARCHAR(500) NULL,
    Placeholder NVARCHAR(200) NULL,
    CreatedTime DATETIME2 NOT NULL DEFAULT GETDATE(),
    ModifiedTime DATETIME2 NOT NULL DEFAULT GETDATE(),
    CONSTRAINT FK_QueryStepParameters_QueryStepId FOREIGN KEY (QueryStepId) REFERENCES QuerySteps(Id) ON DELETE CASCADE
);

-- ===== PHASE 2: MIGRATE DATA (ProjectId moves from Query to QueryStep) =====

-- Migrate existing queries to QuerySteps
-- Each existing query becomes a single QueryStep inheriting the Query's ProjectId
INSERT INTO QuerySteps (QueryId, ProjectId, StepOrder, Name, Description, SqlValue, CreatedTime, ModifiedTime)
SELECT 
    Id,                    -- QueryId
    ProjectId,             -- *** ProjectId migrated from Query to QueryStep ***
    1,                     -- StepOrder (all existing queries become step 1)
    Name,                  -- Name (use query name as step name)
    Description,           -- Description
    SqlValue,              -- SqlValue (move from Query to QueryStep)
    CreatedTime,           -- CreatedTime
    ModifiedTime           -- ModifiedTime
FROM Queries
WHERE SqlValue IS NOT NULL AND SqlValue != '';

-- Migrate existing parameters to QueryStepParameters
INSERT INTO QueryStepParameters (QueryStepId, Name, Type, Description, Placeholder, CreatedTime, ModifiedTime)
SELECT 
    qs.Id,                 -- QueryStepId
    qp.Name,               -- Name
    qp.Type,               -- Type
    qp.Description,        -- Description
    qp.Placeholder,        -- Placeholder
    qp.CreatedTime,        -- CreatedTime
    qp.ModifiedTime        -- ModifiedTime
FROM QueryParameters qp
JOIN QuerySteps qs ON qs.QueryId = qp.QueryId
WHERE qs.StepOrder = 1;    -- Migrate to the first (and only) step

-- ===== PHASE 3: VERIFY DATA MIGRATION =====
-- Validation queries to ensure migration was successful

-- Verify all queries have at least one QueryStep
SELECT 'Queries without steps' as Check, COUNT(*) as Count
FROM Queries q
LEFT JOIN QuerySteps qs ON q.Id = qs.QueryId
WHERE qs.QueryId IS NULL;

-- Verify parameter migration
SELECT 'Original parameters' as Source, COUNT(*) as Count FROM QueryParameters
UNION ALL
SELECT 'Migrated parameters' as Source, COUNT(*) as Count FROM QueryStepParameters;

-- Verify ProjectId migration
SELECT 'Unique ProjectIds in original Queries' as Check, COUNT(DISTINCT ProjectId) as Count FROM Queries
UNION ALL
SELECT 'Unique ProjectIds in QuerySteps' as Check, COUNT(DISTINCT ProjectId) as Count FROM QuerySteps;

-- ===== PHASE 4: CLEAN UP QUERIES TABLE (after verification) =====
-- Remove redundant columns from Queries table

-- Drop foreign key constraint for ProjectId
ALTER TABLE Queries DROP CONSTRAINT FK_Queries_Projects;

-- Remove ProjectId column (now lives in QuerySteps)
ALTER TABLE Queries DROP COLUMN ProjectId;

-- Remove SqlValue column (now lives in QuerySteps) 
ALTER TABLE Queries DROP COLUMN SqlValue;

-- Drop old QueryParameters table (replaced by QueryStepParameters)
DROP TABLE QueryParameters;

-- ===== PHASE 5: ADD PERFORMANCE INDEXES =====
CREATE INDEX IX_QuerySteps_QueryId_StepOrder ON QuerySteps(QueryId, StepOrder);
CREATE INDEX IX_QuerySteps_ProjectId ON QuerySteps(ProjectId);
CREATE INDEX IX_QueryStepParameters_QueryStepId ON QueryStepParameters(QueryStepId);

-- ===== PHASE 6: UPDATE ENTITY FRAMEWORK MIGRATION =====
-- This ensures EF Core knows about the schema changes

-- Update model snapshot to reflect new schema
-- Remove ProjectId and SqlValue from Query entity configuration
-- Add QueryStep and QueryStepParameter entity configurations
```

### Entity Framework Migration Class
```csharp
public partial class EnableCrossProjectQueryChains : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Create QuerySteps table with ProjectId
        migrationBuilder.CreateTable(
            name: "QuerySteps",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                QueryId = table.Column<int>(type: "int", nullable: false),
                ProjectId = table.Column<int>(type: "int", nullable: false), // KEY CHANGE: ProjectId here now
                StepOrder = table.Column<int>(type: "int", nullable: false),
                Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                SqlValue = table.Column<string>(type: "nvarchar(max)", nullable: false),
                CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()"),
                ModifiedTime = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_QuerySteps", x => x.Id);
                table.ForeignKey(
                    name: "FK_QuerySteps_Queries_QueryId",
                    column: x => x.QueryId,
                    principalTable: "Queries",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_QuerySteps_Projects_ProjectId", // NEW: Foreign key to Projects
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.UniqueConstraint("UQ_QuerySteps_QueryId_StepOrder", x => new { x.QueryId, x.StepOrder });
            });

        // Create QueryStepParameters table
        migrationBuilder.CreateTable(
            name: "QueryStepParameters",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                QueryStepId = table.Column<int>(type: "int", nullable: false),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Type = table.Column<int>(type: "int", nullable: false),
                Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                Placeholder = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()"),
                ModifiedTime = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_QueryStepParameters", x => x.Id);
                table.ForeignKey(
                    name: "FK_QueryStepParameters_QuerySteps_QueryStepId",
                    column: x => x.QueryStepId,
                    principalTable: "QuerySteps",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        // Data migration: Move existing queries to QuerySteps
        migrationBuilder.Sql(@"
            -- Migrate existing queries to QuerySteps (ProjectId moves from Query to QueryStep)
            INSERT INTO QuerySteps (QueryId, ProjectId, StepOrder, Name, Description, SqlValue, CreatedTime, ModifiedTime)
            SELECT Id, ProjectId, 1, Name, Description, SqlValue, CreatedTime, ModifiedTime
            FROM Queries
            WHERE SqlValue IS NOT NULL AND SqlValue != '';
            
            -- Migrate existing parameters to QueryStepParameters
            INSERT INTO QueryStepParameters (QueryStepId, Name, Type, Description, Placeholder, CreatedTime, ModifiedTime)
            SELECT qs.Id, qp.Name, qp.Type, qp.Description, qp.Placeholder, qp.CreatedTime, qp.ModifiedTime
            FROM QueryParameters qp
            JOIN QuerySteps qs ON qs.QueryId = qp.QueryId
            WHERE qs.StepOrder = 1;
        ");

        // Create indexes for performance
        migrationBuilder.CreateIndex(
            name: "IX_QuerySteps_QueryId_StepOrder",
            table: "QuerySteps",
            columns: new[] { "QueryId", "StepOrder" });

        migrationBuilder.CreateIndex(
            name: "IX_QuerySteps_ProjectId", // NEW: Index on ProjectId for cross-project queries
            table: "QuerySteps",
            column: "ProjectId");

        migrationBuilder.CreateIndex(
            name: "IX_QueryStepParameters_QueryStepId",
            table: "QueryStepParameters",
            column: "QueryStepId");

        // Remove old columns from Queries table
        migrationBuilder.DropForeignKey(
            name: "FK_Queries_Projects_ProjectId",
            table: "Queries");

        migrationBuilder.DropColumn(
            name: "ProjectId", // REMOVED: ProjectId now lives in QuerySteps
            table: "Queries");

        migrationBuilder.DropColumn(
            name: "SqlValue", // REMOVED: SqlValue now lives in QuerySteps
            table: "Queries");

        // Drop old QueryParameters table
        migrationBuilder.DropTable(
            name: "QueryParameters"); // REPLACED: by QueryStepParameters
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Reverse migration - restore original schema
        
        // Re-add columns to Queries table
        migrationBuilder.AddColumn<int>(
            name: "ProjectId",
            table: "Queries",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "SqlValue",
            table: "Queries",
            type: "nvarchar(max)",
            nullable: false,
            defaultValue: "");

        // Recreate QueryParameters table
        migrationBuilder.CreateTable(
            name: "QueryParameters",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                QueryId = table.Column<int>(type: "int", nullable: false),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Type = table.Column<int>(type: "int", nullable: false),
                Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                Placeholder = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                ModifiedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_QueryParameters", x => x.Id);
                table.ForeignKey(
                    name: "FK_QueryParameters_Queries_QueryId",
                    column: x => x.QueryId,
                    principalTable: "Queries",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        // Migrate data back to original structure
        migrationBuilder.Sql(@"
            -- Restore ProjectId and SqlValue to Queries table (from first QueryStep)
            UPDATE q 
            SET ProjectId = qs.ProjectId, SqlValue = qs.SqlValue
            FROM Queries q
            JOIN QuerySteps qs ON qs.QueryId = q.Id
            WHERE qs.StepOrder = 1;
            
            -- Restore parameters to QueryParameters table
            INSERT INTO QueryParameters (QueryId, Name, Type, Description, Placeholder, CreatedTime, ModifiedTime)
            SELECT qs.QueryId, qsp.Name, qsp.Type, qsp.Description, qsp.Placeholder, qsp.CreatedTime, qsp.ModifiedTime
            FROM QueryStepParameters qsp
            JOIN QuerySteps qs ON qs.Id = qsp.QueryStepId
            WHERE qs.StepOrder = 1;
        ");

        // Re-add foreign key constraint
        migrationBuilder.CreateIndex(
            name: "IX_Queries_ProjectId",
            table: "Queries",
            column: "ProjectId");

        migrationBuilder.AddForeignKey(
            name: "FK_Queries_Projects_ProjectId",
            table: "Queries",
            column: "ProjectId",
            principalTable: "Projects",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);

        // Drop new tables
        migrationBuilder.DropTable("QueryStepParameters");
        migrationBuilder.DropTable("QuerySteps");
    }
}
```

## Real-World Use Cases

### 1. E-commerce Cross-Database Analytics
```sql
-- Step 1: Get user data from PostgreSQL user database
SELECT user_id, email, registration_date, country 
FROM users 
WHERE registration_date >= '2024-01-01'

-- Step 2: Get order data from SQL Server transaction database  
SELECT user_id, order_id, order_total, order_date
FROM orders
WHERE order_date >= '2024-01-01'

-- Step 3: Get product analytics from MySQL analytics database
SELECT product_id, category, avg_rating, review_count
FROM product_analytics
WHERE last_updated >= '2024-01-01'

-- Final Query: Cross-database join executed on SQL Server
SELECT 
    u.country,
    u.email,
    o.order_total,
    p.category,
    p.avg_rating
FROM @result1 u
JOIN @result2 o ON u.user_id = o.user_id  
JOIN @result3 p ON o.product_id = p.product_id
WHERE o.order_total > 100
ORDER BY u.country, o.order_total DESC
```

### 2. Data Migration Validation
```sql
-- Step 1: Get records from legacy Oracle system
SELECT customer_id, name, email, phone FROM legacy_customers

-- Step 2: Get migrated records from new PostgreSQL system  
SELECT customer_id, name, email, phone FROM new_customers

-- Final Query: Find discrepancies between old and new systems
SELECT 
    COALESCE(legacy.customer_id, new.customer_id) as customer_id,
    CASE 
        WHEN legacy.customer_id IS NULL THEN 'Missing in legacy'
        WHEN new.customer_id IS NULL THEN 'Missing in new system'
        WHEN legacy.email != new.email THEN 'Email mismatch'
        ELSE 'Match'
    END as status
FROM @result1 legacy
FULL OUTER JOIN @result2 new ON legacy.customer_id = new.customer_id
WHERE legacy.customer_id IS NULL 
   OR new.customer_id IS NULL 
   OR legacy.email != new.email
```

### 3. Multi-Environment Performance Comparison
```sql
-- Step 1: Production database metrics
SELECT table_name, avg_query_time, total_queries 
FROM performance_metrics 
WHERE date = CURRENT_DATE

-- Step 2: Staging database metrics
SELECT table_name, avg_query_time, total_queries 
FROM performance_metrics 
WHERE date = CURRENT_DATE

-- Final Query: Compare performance between environments
SELECT 
    prod.table_name,
    prod.avg_query_time as prod_avg,
    stage.avg_query_time as stage_avg,
    ROUND((prod.avg_query_time - stage.avg_query_time) / stage.avg_query_time * 100, 2) as perf_diff_pct
FROM @result1 prod
JOIN @result2 stage ON prod.table_name = stage.table_name
WHERE ABS(prod.avg_query_time - stage.avg_query_time) / stage.avg_query_time > 0.1
ORDER BY perf_diff_pct DESC
```

## Benefits of Cross-Project Architecture

### Technical Benefits
- **Maximum Flexibility**: Each step targets the optimal database for its task
- **Zero Data Movement**: Query data where it lives, reduce network overhead
- **Database Engine Optimization**: Use PostgreSQL for analytics, SQL Server for transactions
- **Unified Interface**: One tool for all database interactions across the organization
- **Scalable Architecture**: Add new databases without system changes

### Business Benefits
- **Faster Time to Insights**: No need to wait for ETL processes
- **Reduced Data Duplication**: Query original sources directly
- **Enhanced Data Governance**: Maintain data in system of record
- **Cost Optimization**: Avoid expensive data warehouse solutions for ad-hoc queries
- **Improved Compliance**: Audit trails show exact data sources

### Operational Benefits
- **Simplified Infrastructure**: No complex data pipelines needed
- **Real-time Analysis**: Always query latest data from source systems
- **Reduced Maintenance**: No duplicate data synchronization processes
- **Better Performance**: Query optimization at source database level
- **Enhanced Security**: Data stays in authorized systems

## Performance Considerations

### Memory Management
- **Virtual Table Limits**: Limit each step to 1000 rows by default (configurable)
- **Automatic Cleanup**: VirtualTableManager disposes resources after execution
- **Memory Monitoring**: Log estimated memory usage for cross-database scenarios
- **Garbage Collection**: Explicit disposal and GC hints for large result sets

### Network Optimization  
- **Connection Pooling**: Reuse database connections within execution context
- **Parallel Execution**: Execute independent steps in parallel where possible
- **Result Compression**: Compress virtual table data in memory
- **Timeout Handling**: Per-step and total execution timeouts

### Query Optimization
- **Engine-Specific SQL**: Generate optimal SQL for each target database engine
- **Index Awareness**: Log recommendations for missing indexes on source tables
- **Execution Plan Analysis**: Capture and log execution plans for optimization
- **Cached Compilation**: Cache compiled SQL for repeated executions

## Security and Compliance

### Access Control
- **Project-Level Security**: Each step respects source database permissions
- **User Authentication**: Inherit user context for database connections
- **Audit Logging**: Log all cross-database query executions
- **Data Classification**: Track data sensitivity across database boundaries

### Data Protection
- **Encryption in Transit**: Secure connections to all databases
- **Memory Protection**: Secure virtual table storage in memory
- **PII Handling**: Identify and protect personally identifiable information
- **Retention Policies**: Automatic cleanup of sensitive virtual table data

## Monitoring and Observability

### Execution Metrics
- **Cross-Database Query Tracking**: Monitor queries spanning multiple databases
- **Performance by Database Engine**: Track execution times per database type
- **Memory Usage Patterns**: Monitor virtual table memory consumption
- **Error Rate Analysis**: Track failures by database engine and project

### Business Intelligence
- **Usage Analytics**: Which cross-database patterns are most common
- **Performance Insights**: Identify optimization opportunities
- **Adoption Metrics**: Track growth in cross-project query usage
- **Cost Analysis**: Monitor resource usage across different databases

## Future Enhancements

### Advanced Features
- **Cached Virtual Tables**: Cache frequently used intermediate results
- **Incremental Updates**: Update virtual tables with only changed data
- **Parallel Step Execution**: Execute independent steps simultaneously
- **Query Plan Optimization**: Analyze and optimize cross-database execution plans

### AI and Machine Learning
- **Query Recommendation Engine**: Suggest optimal step sequences
- **Automatic Index Suggestions**: Recommend indexes based on query patterns  
- **Performance Prediction**: Predict execution times for complex cross-database queries
- **Data Lineage Tracking**: Automatically track data flow across systems

## Conclusion

The Cross-Project QueryStep Architecture represents a paradigm shift in database query capabilities. By moving ProjectId from Query to QueryStep, we enable revolutionary cross-database analytics while maintaining complete backward compatibility.

This architecture empowers users to:
- **Break down data silos** by querying across organizational boundaries
- **Reduce time to insights** by eliminating complex ETL processes  
- **Optimize resource usage** by leveraging the strengths of different database engines
- **Maintain data governance** by keeping data in systems of record
- **Scale analytically** without infrastructure complexity

The unified QueryStep model ensures that whether users are running simple single-database queries or complex cross-database analytics, they have a consistent, powerful, and intuitive interface that grows with their analytical needs.

This is not just an architectural enhancement—it's a fundamental enabler for data-driven decision making in complex, heterogeneous database environments.