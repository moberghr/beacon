# Migration UI Component Contracts

## Page Components

### MigrationListPage.razor
```csharp
@page "/data-migration"
@using Semantico.Core.Features.DataMigration

<SemanticoPageHeader Title="Data Migration" 
                     SubTitle="Manage data migration jobs and view execution history" />

<MudContainer MaxWidth="MaxWidth.False">
    <MigrationJobList OnJobSelected="NavigateToDetails" 
                     OnJobExecute="ExecuteJob"
                     OnJobDelete="DeleteJob" />
</MudContainer>

@code {
    // Component behavior specifications
}
```

**Properties & Events**:
- No parameters (root page)
- Handles navigation to job details
- Handles job execution requests
- Handles job deletion requests

### CreateMigrationJobPage.razor
```csharp
@page "/data-migration/create"
@page "/data-migration/edit/{JobId:int}"
@using Semantico.Core.Features.DataMigration

<SemanticoPageHeader Title="@(IsEdit ? "Edit Migration Job" : "Create Migration Job")" 
                     SubTitle="Configure data migration parameters" />

<MudContainer MaxWidth="MaxWidth.Large">
    <MigrationJobForm Job="@CurrentJob" 
                     IsEdit="@IsEdit"
                     OnSave="SaveJob"
                     OnCancel="NavigateBack"
                     OnValidate="ValidateJob" />
</MudContainer>

@code {
    [Parameter] public int? JobId { get; set; }
    
    private bool IsEdit => JobId.HasValue;
    private MigrationJobDto? CurrentJob { get; set; }
    
    // Component behavior specifications
}
```

**Properties & Events**:
- `JobId` parameter for edit mode
- Handles save/cancel actions
- Manages job validation

### MigrationHistoryPage.razor
```csharp
@page "/data-migration/history"
@page "/data-migration/history/{JobId:int}"
@using Semantico.Core.Features.DataMigration

<SemanticoPageHeader Title="Migration History" 
                     SubTitle="View execution history and performance metrics" />

<MudContainer MaxWidth="MaxWidth.False">
    <MigrationExecutionHistory JobId="@JobId"
                              OnExecutionSelected="ViewExecutionDetails" />
</MudContainer>

@code {
    [Parameter] public int? JobId { get; set; }
    
    // Component behavior specifications
}
```

**Properties & Events**:
- Optional `JobId` parameter to filter by specific job
- Handles execution detail navigation

## UI Components

### MigrationJobList.razor
```csharp
<MudCard>
    <MudCardHeader>
        <CardHeaderContent>
            <div class="d-flex justify-space-between align-center">
                <MudText Typo="Typo.h6">Migration Jobs</MudText>
                <MudButton Variant="Variant.Filled" 
                          Color="Color.Primary"
                          StartIcon="Icons.Material.Filled.Add"
                          Href="/data-migration/create">
                    Create Migration Job
                </MudButton>
            </div>
        </CardHeaderContent>
    </MudCardHeader>
    
    <MudCardContent>
        <MudDataGrid T="MigrationJobDto" 
                    Items="@Jobs"
                    Loading="@Loading"
                    ServerData="LoadServerData">
            <!-- Column definitions -->
        </MudDataGrid>
    </MudCardContent>
</MudCard>

@code {
    [Parameter] public EventCallback<MigrationJobDto> OnJobSelected { get; set; }
    [Parameter] public EventCallback<MigrationJobDto> OnJobExecute { get; set; }
    [Parameter] public EventCallback<MigrationJobDto> OnJobDelete { get; set; }
    
    private List<MigrationJobDto> Jobs = new();
    private bool Loading = false;
    
    // Component behavior specifications
}
```

**Column Specifications**:
- Name (with edit link)
- Source Project
- Destination Project & Table
- Schedule (cron expression or "Manual")
- Status (Enabled/Disabled)
- Last Execution (date/time + status)
- Actions (Execute, Edit, Delete)

### MigrationJobForm.razor
```csharp
<EditForm Model="@Job" OnValidSubmit="@HandleValidSubmit">
    <MudCard>
        <MudCardContent>
            <MudGrid>
                <!-- Basic Information -->
                <MudItem xs="12" md="6">
                    <MudTextField @bind-Value="Job.Name" 
                                 Label="Job Name" 
                                 Required="true" 
                                 MaxLength="200" />
                </MudItem>
                
                <MudItem xs="12">
                    <MudTextField @bind-Value="Job.Description" 
                                 Label="Description" 
                                 Required="true" 
                                 Lines="3"
                                 MaxLength="1000" />
                </MudItem>
                
                <!-- Source Configuration -->
                <MudItem xs="12" md="6">
                    <ProjectSelector @bind-Value="Job.ProjectId" 
                                   Label="Source Project" 
                                   Required="true" />
                </MudItem>
                
                <MudItem xs="12">
                    <QueryEditor @bind-Value="Job.QueryText" 
                               ProjectId="Job.ProjectId"
                               OnValidate="ValidateQuery"
                               Label="Source Query" />
                </MudItem>
                
                <!-- Destination Configuration -->
                <MudItem xs="12" md="6">
                    <ProjectSelector @bind-Value="Job.DestinationProjectId" 
                                   Label="Destination Project" 
                                   Required="true" />
                </MudItem>
                
                <MudItem xs="12" md="6">
                    <MudTextField @bind-Value="Job.DestinationTable" 
                                 Label="Destination Table" 
                                 Required="true" />
                </MudItem>
                
                <MudItem xs="12" md="6">
                    <MudSelect @bind-Value="Job.Mode" Label="Migration Mode">
                        <MudSelectItem Value="MigrationMode.Insert">Insert Only</MudSelectItem>
                        <MudSelectItem Value="MigrationMode.Upsert">Insert or Update</MudSelectItem>
                        <MudSelectItem Value="MigrationMode.Truncate">Truncate & Insert</MudSelectItem>
                        <MudSelectItem Value="MigrationMode.SyncDelete">Sync with Delete</MudSelectItem>
                    </MudSelect>
                </MudItem>
                
                <!-- Execution Options -->
                <MudItem xs="12" md="6">
                    <MudTextField @bind-Value="Job.Schedule" 
                                 Label="Schedule (Cron)" 
                                 HelperText="Leave empty for manual execution" />
                </MudItem>
                
                <MudItem xs="12" md="3">
                    <MudNumericField @bind-Value="Job.MaxRetries" 
                                    Label="Max Retries" 
                                    Min="0" Max="10" />
                </MudItem>
                
                <MudItem xs="12" md="3">
                    <MudNumericField @bind-Value="Job.TimeoutMinutes" 
                                    Label="Timeout (Minutes)" 
                                    Min="1" Max="1440" />
                </MudItem>
                
                <!-- Advanced Options -->
                <MudItem xs="12">
                    <MudCheckBox @bind-Checked="Job.ValidateBeforeExecution" 
                                Label="Validate query before execution" />
                </MudItem>
                
                <MudItem xs="12">
                    <MudTextField @bind-Value="Job.TransformationScript" 
                                 Label="Data Transformation (@result syntax)" 
                                 Lines="5"
                                 HelperText="Optional: Use @result1, @result2 syntax for data transformation" />
                </MudItem>
            </MudGrid>
        </MudCardContent>
        
        <MudCardActions>
            <MudButton ButtonType="ButtonType.Submit" 
                      Variant="Variant.Filled" 
                      Color="Color.Primary"
                      Disabled="@Loading">
                @(IsEdit ? "Update" : "Create")
            </MudButton>
            <MudButton OnClick="@OnCancel" 
                      Variant="Variant.Text">
                Cancel
            </MudButton>
            <MudButton OnClick="@ValidateOnly" 
                      Variant="Variant.Outlined"
                      Color="Color.Info"
                      Disabled="@Loading">
                Validate
            </MudButton>
        </MudCardActions>
    </MudCard>
</EditForm>

@code {
    [Parameter] public MigrationJobDto Job { get; set; } = new();
    [Parameter] public bool IsEdit { get; set; }
    [Parameter] public EventCallback<MigrationJobDto> OnSave { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }
    [Parameter] public EventCallback<string> OnValidate { get; set; }
    
    private bool Loading = false;
    
    // Component behavior specifications
}
```

### MigrationExecutionHistory.razor
```csharp
<MudCard>
    <MudCardHeader>
        <CardHeaderContent>
            <MudText Typo="Typo.h6">Execution History</MudText>
        </CardHeaderContent>
        <CardHeaderActions>
            <ExecutionFilters @bind-JobId="JobId"
                            @bind-Status="StatusFilter"
                            @bind-DateRange="DateRangeFilter"
                            OnFiltersChanged="RefreshData" />
        </CardHeaderActions>
    </MudCardHeader>
    
    <MudCardContent>
        <MudDataGrid T="MigrationExecutionDto" 
                    Items="@Executions"
                    Loading="@Loading"
                    ServerData="LoadServerData">
            <!-- Column definitions -->
        </MudDataGrid>
    </MudCardContent>
</MudCard>

@code {
    [Parameter] public int? JobId { get; set; }
    [Parameter] public EventCallback<MigrationExecutionDto> OnExecutionSelected { get; set; }
    
    private List<MigrationExecutionDto> Executions = new();
    private bool Loading = false;
    private MigrationStatus? StatusFilter;
    private DateRange? DateRangeFilter;
    
    // Component behavior specifications
}
```

**Column Specifications**:
- Migration Job Name (with link to job details)
- Started At (date/time)
- Status (with color coding)
- Duration
- Rows Read/Written/Failed
- Performance (rows/sec)
- Actions (View Details, Retry if failed)

## Supporting Components

### ProjectSelector.razor
```csharp
<MudSelect T="int" @bind-Value="Value" Label="@Label" Required="@Required">
    @if (Projects != null)
    {
        @foreach (var project in Projects)
        {
            <MudSelectItem Value="project.Id">@project.Name (@project.Engine)</MudSelectItem>
        }
    }
</MudSelect>

@code {
    [Parameter] public int Value { get; set; }
    [Parameter] public EventCallback<int> ValueChanged { get; set; }
    [Parameter] public string Label { get; set; } = "Project";
    [Parameter] public bool Required { get; set; }
    
    private List<ProjectDto>? Projects;
    
    // Component behavior specifications
}
```

### QueryEditor.razor
```csharp
<MudCard>
    <MudCardContent>
        <MudTextField @bind-Value="Value" 
                     Label="@Label" 
                     Lines="10" 
                     Variant="Variant.Outlined"
                     Class="mud-input-code" />
        
        @if (ProjectId > 0)
        {
            <div class="d-flex justify-space-between align-center mt-2">
                <MudButton OnClick="ValidateQuery" 
                          Variant="Variant.Outlined" 
                          Size="Size.Small"
                          StartIcon="Icons.Material.Filled.CheckCircle">
                    Validate Query
                </MudButton>
                
                @if (ValidationResult != null)
                {
                    <ValidationResultDisplay Result="@ValidationResult" />
                }
            </div>
        }
    </MudCardContent>
</MudCard>

@code {
    [Parameter] public string Value { get; set; } = "";
    [Parameter] public EventCallback<string> ValueChanged { get; set; }
    [Parameter] public string Label { get; set; } = "Query";
    [Parameter] public int ProjectId { get; set; }
    [Parameter] public EventCallback<string> OnValidate { get; set; }
    
    private ValidationResult? ValidationResult;
    
    // Component behavior specifications
}
```

### ExecutionStatusChip.razor
```csharp
<MudChip Color="@GetStatusColor(Status)" 
         Size="Size.Small" 
         Icon="@GetStatusIcon(Status)">
    @Status.ToString()
</MudChip>

@code {
    [Parameter] public MigrationStatus Status { get; set; }
    
    private Color GetStatusColor(MigrationStatus status) => status switch
    {
        MigrationStatus.Completed => Color.Success,
        MigrationStatus.Failed => Color.Error,
        MigrationStatus.Running => Color.Info,
        MigrationStatus.Queued => Color.Default,
        MigrationStatus.Cancelled => Color.Warning,
        MigrationStatus.PartialSuccess => Color.Warning,
        _ => Color.Default
    };
    
    private string GetStatusIcon(MigrationStatus status) => status switch
    {
        MigrationStatus.Completed => Icons.Material.Filled.CheckCircle,
        MigrationStatus.Failed => Icons.Material.Filled.Error,
        MigrationStatus.Running => Icons.Material.Filled.Refresh,
        MigrationStatus.Queued => Icons.Material.Filled.Schedule,
        MigrationStatus.Cancelled => Icons.Material.Filled.Cancel,
        MigrationStatus.PartialSuccess => Icons.Material.Filled.Warning,
        _ => Icons.Material.Filled.Help
    };
}
```

## Navigation Integration

### MainLayout Integration
Add navigation menu item:
```csharp
<MudNavLink Href="/data-migration" 
           Match="NavLinkMatch.Prefix"
           Icon="Icons.Material.Filled.SwapHoriz">
    Data Migration
</MudNavLink>
```

### Breadcrumb Integration
```csharp
// Migration List: Home > Data Migration
// Create Job: Home > Data Migration > Create Job
// Edit Job: Home > Data Migration > Edit Job ({JobName})
// History: Home > Data Migration > History
```

## Component Behavior Specifications

### MigrationJobList Behavior
1. **Data Loading**: Server-side pagination with filtering and sorting
2. **Actions**: 
   - Execute: Confirm dialog → Call handler → Show progress
   - Edit: Navigate to edit page with job ID
   - Delete: Confirm dialog → Archive job → Refresh list
3. **Status Display**: Color-coded status indicators
4. **Performance**: Virtual scrolling for large lists

### MigrationJobForm Behavior
1. **Validation**: 
   - Client-side validation for required fields
   - Server-side validation for query syntax and connectivity
   - Real-time feedback on field changes
2. **Query Editor**: 
   - Syntax highlighting for SQL
   - Validation against selected project
   - Test execution capability
3. **Auto-save**: Draft functionality for long forms

### MigrationExecutionHistory Behavior
1. **Real-time Updates**: SignalR integration for running executions
2. **Performance Metrics**: Charts and graphs for execution trends
3. **Error Analysis**: Detailed error logs and stack traces
4. **Export**: CSV/Excel export for reporting