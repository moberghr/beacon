using System.Text.Json;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;
using Beacon.Core.SavedQueries;
using Beacon.Tests.Unit.HostDocs;

namespace Beacon.Tests.Unit.SavedQueryTools;

/// <summary>
/// Seeds a <see cref="DocsStore"/> with projects, data sources, queries, versions and approval requests — navigations
/// wired the way EF would load them, so the handlers' LINQ runs over the list-backed doubles (no database, §4.7).
/// </summary>
internal sealed class SavedQueryTestData
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DocsStore Store { get; } = new();

    public Project Project(int id, string name, params DataSource[] dataSources)
    {
        var project = new Project { Id = id, Name = name };
        Store.Projects.Add(project);
        foreach (var dataSource in dataSources)
        {
            Store.ListFor<ProjectDataSource>().Add(new ProjectDataSource
            {
                Id = Store.NextId(),
                ProjectId = id,
                DataSourceId = dataSource.Id,
                Project = project,
                DataSource = dataSource
            });
        }

        return project;
    }

    public DataSource DataSource(int id, string name, string? hostManagedKey = null)
    {
        var dataSource = new DataSource
        {
            Id = id,
            Name = name,
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = "encrypted",
            HostManagedKey = hostManagedKey
        };
        Store.ListFor<DataSource>().Add(dataSource);

        return dataSource;
    }

    /// <summary>A query whose active version is <paramref name="versionStatus"/> with an approval in <paramref name="approval"/> (null → none).</summary>
    public Query Query(
        int id,
        string? toolName,
        IReadOnlyList<QueryStepSnapshot> steps,
        QueryVersionStatus versionStatus = QueryVersionStatus.Active,
        ApprovalStatus? approval = ApprovalStatus.Approved,
        bool enabled = true,
        string? finalQuery = null,
        string? toolDescription = null)
    {
        var query = new Query
        {
            Id = id,
            Name = $"Query {id}",
            Description = $"Description of query {id}",
            McpToolName = toolName,
            McpToolDescription = toolDescription,
            McpToolEnabled = enabled
        };
        Store.ListFor<Query>().Add(query);

        var version = AddVersion(query, 1, versionStatus, steps, finalQuery, approval);
        if (versionStatus == QueryVersionStatus.Active)
        {
            query.ActiveVersionId = version.Id;
            query.ActiveVersion = version;
        }

        return query;
    }

    public QueryVersion AddVersion(
        Query query,
        int number,
        QueryVersionStatus status,
        IReadOnlyList<QueryStepSnapshot> steps,
        string? finalQuery = null,
        ApprovalStatus? approval = null)
    {
        var version = new QueryVersion
        {
            Id = Store.NextId(),
            QueryId = query.Id,
            Query = query,
            VersionNumber = number,
            Status = status,
            Name = $"{query.Name} v{number}",
            Description = query.Description,
            FinalQuery = finalQuery,
            StepsJson = JsonSerializer.Serialize(steps, JsonOptions)
        };
        Store.ListFor<QueryVersion>().Add(version);

        if (approval.HasValue)
        {
            Store.ListFor<QueryApprovalRequest>().Add(new QueryApprovalRequest
            {
                Id = Store.NextId(),
                QueryId = query.Id,
                Query = query,
                QueryVersionId = version.Id,
                QueryVersion = version,
                Status = approval.Value
            });
        }

        return version;
    }

    public static QueryStepSnapshot Step(int order, int dataSourceId, string sql, params QueryStepParameterSnapshot[] parameters) =>
        new()
        {
            StepOrder = order,
            DataSourceId = dataSourceId,
            DataSourceName = $"ds{dataSourceId}",
            SqlValue = sql,
            Parameters = parameters.ToList()
        };

    public static QueryStepParameterSnapshot Parameter(string name, ParameterType type, string? description = null) =>
        new() { Name = name, Type = type, Description = description, Placeholder = "{" + name + "}" };

    public static SavedQueryToolDefinition Tool(
        string name,
        IReadOnlyList<QueryStepSnapshot> steps,
        IReadOnlyList<int> projectIds,
        string? finalQuery = null,
        int queryId = 1,
        int versionId = 100)
    {
        var shape = SavedQueryToolRules.Inspect(JsonSerializer.Serialize(steps, JsonOptions));

        return new SavedQueryToolDefinition(
            queryId,
            name,
            $"Title of {name}",
            $"Description of {name}",
            versionId,
            3,
            shape.Steps,
            finalQuery,
            shape.Parameters,
            shape.Steps
                .Select(x => x.DataSourceId)
                .Distinct()
                .ToList(),
            projectIds);
    }
}
