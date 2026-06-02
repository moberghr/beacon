# Beacon Coding Style Guide

This document provides detailed coding style guidelines for the Beacon solution. Follow these patterns when generating or modifying code to maintain consistency.

## Naming Conventions
- **Classes/Types**: PascalCase (e.g., `ProjectService`, `QueryParameter`)
- **Interfaces**: Prefix with "I" + PascalCase (e.g., `IAdapter`, `IProjectService`)
- **Methods**: PascalCase (e.g., `CreateProject`, `DeleteQuery`)
- **Properties**: PascalCase (e.g., `Name`, `ConnectionString`)
- **Private fields**: Underscore prefix + camelCase (e.g., `_context`, `_dataGrid`)
- **Local variables**: camelCase (e.g., `project`, `queryParameter`)
- **Parameters**: camelCase (e.g., `queryData`, `cancellationToken`)

## Formatting and Structure
- **Indentation**: Use 4 spaces
- **Braces**: "Allman style" - opening braces on their own lines
```csharp
public void Method()
{
    if (condition)
    {
        // code
    }
}
```
- **Line spacing**: Single blank line between methods
- **Namespaces**: No indentation for namespace definitions
- **LINQ**: Indent chain calls with each operator on a new line
```csharp
var results = entities
    .Where(x => x.IsActive)
    .Select(x => new ResultData
    {
        Id = x.Id,
        Name = x.Name
    })
    .ToList();
```
- **Required properties**: Use the `required` modifier for non-nullable properties
- **File organization**: Place interfaces at the top of the file before implementation classes

## Architecture and Patterns
- **Clean Architecture**: Follow Clean Architecture principles with Core containing domain logic
- **Services**: Create interfaces and implementations for domain services
```csharp
public interface IProjectService
{
    Task<BaseResponse> CreateProject(ProjectData projectData, CancellationToken cancellationToken);
}

internal class ProjectService : IProjectService
{
    // Implementation
}
```
- **Models**: Use specific models (DTOs) for data transfer between layers
- **Entities**: Define domain entities in the Data folder with appropriate relationships
- **Validation**: Use validator classes for domain validation logic
- **Extension methods**: Create extension methods for common operations (e.g., LINQ extensions)

## Error Handling
- **Domain Exceptions**: Use `BeaconException` class for domain-specific errors
```csharp
if (condition)
{
    throw new BeaconException("Meaningful error message");
}
```
- **Validation**: Perform early validation before processing operations
- **Result objects**: Return `BaseResponse` objects from service methods to indicate success/failure

## Language Features
- **Async/await**: Use consistently with Task return types and CancellationToken parameters
```csharp
public async Task<BaseResponse> CreateProject(ProjectData projectData, CancellationToken cancellationToken)
{
    // Async implementation
    await _context.SaveChangesAsync(cancellationToken);
}
```
- **LINQ**: Use for data queries and transformations with proper indentation
- **Null handling**: Use null conditional operators (`?.`) and null coalescing operators (`??`)
- **String interpolation**: Use for string formatting (`$"Value: {value}"`)
- **Type inference**: Use `var` for local variables when type is obvious from initialization

## Database and Entity Framework
- **Migrations**: Create migrations for schema changes
```bash
dotnet ef migrations add MigrationName --project Beacon.Core --startup-project Beacon.SampleProject
```
- **Entity configuration**: Use Fluent API or attributes for entity configuration
- **Queries**: Use AsSplitQuery() for loading related entities when appropriate
```csharp
_context.Entities
    .AsSplitQuery()
    .Where(x => x.IsActive)
    .Include(x => x.RelatedEntities)
    .ToListAsync();
```
- **Context**: Inject DbContext through constructors

## UI Components (Blazor)
- **Component structure**: Use BasePageComponent as base class for pages
```csharp
@inherits BasePageComponent
```
- **Event handling**: Use async methods for event handlers
- **Data binding**: Use two-way binding (`@bind-*`) for form inputs
- **Component organization**: Group related components in feature folders

## Implementation Examples

### Service Implementation
```csharp
internal class ProjectService : IProjectService
{
    private readonly BeaconContext _context;

    public ProjectService(BeaconContext context)
    {
        _context = context;
    }

    public async Task<BaseResponse> CreateProject(ProjectData projectData, CancellationToken cancellationToken)
    {
        // Validation
        if (string.IsNullOrEmpty(projectData.Name))
        {
            throw new BeaconException("Project name is required");
        }

        var project = new Project
        {
            Name = projectData.Name,
            ConnectionString = projectData.ConnectionString,
            DatabaseEngineType = projectData.DatabaseEngineType
        };

        _context.Projects.Add(project);
        await _context.SaveChangesAsync(cancellationToken);

        return new BaseResponse
        {
            Success = true,
            Message = "Project created successfully"
        };
    }
}
```

### Entity Definition
```csharp
internal class Project : ArchivableBaseEntity
{
    public required string Name { get; set; }

    public required string ConnectionString { get; set; }

    public required DatabaseEngineType DatabaseEngineType { get; set; }

    public List<Query> Queries { get; set; } = new();
}
```

### Blazor Component
```csharp
@page "/beacon/projects"
@inherits BasePageComponent
@using Beacon.Core.Models.Projects

<BeaconPageTitle Title="Projects" />

<MudContainer Class="my-4 px-4">
    <BeaconPageHeader Icon="@Icons.Material.Filled.FolderOpen" Title="Projects" ButtonText="Add Project" OnClick="OpenAddProjectDialog"/>
    <BeaconPageAlert Text="List of available projects." />
    
    <MudDataGrid @ref="_dataGrid" ServerData="ServerReload" T="ProjectData" Hover="true">
        <Columns>
            <PropertyColumn Property="x => x.Id" Title="Id"/>
            <PropertyColumn Property="x => x.Name" Title="Name"/>
            <PropertyColumn Property="x => x.DatabaseEngineType" Title="Database Type"/>
            <TemplateColumn CellClass="d-flex justify-end">
                <CellTemplate>
                    <MudIconButton Icon="@Icons.Material.Filled.Delete" 
                                   Size="Size.Small" 
                                   OnClick="@(() => DeleteProject(context.Item))" />
                </CellTemplate>
            </TemplateColumn>
        </Columns>
        <PagerContent>
            <MudDataGridPager T="ProjectData" />
        </PagerContent>
    </MudDataGrid>
</MudContainer>

@code {
    private MudDataGrid<ProjectData>? _dataGrid;
    
    private async Task<GridData<ProjectData>> ServerReload(GridState<ProjectData> state)
    {
        // Implementation
    }
    
    private async Task DeleteProject(ProjectData? project)
    {
        // Implementation
    }
}
```