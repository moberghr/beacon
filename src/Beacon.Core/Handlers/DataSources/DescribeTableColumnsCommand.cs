using Beacon.Core.Authorization;
using MediatR;

namespace Beacon.Core.Handlers.DataSources;

/// <summary>
/// Asks the LLM to describe the columns of one table that have no description and no pending column-description patch (at
/// most <see cref="MaxColumns"/>, in ordinal order) and stores each answer as a proposed patch in <see cref="ProjectId"/>;
/// nothing is written to the catalog until a curator applies a patch. The project must link the data source. The handler
/// lives in <c>Beacon.AI</c>, which owns the LLM provider.
/// </summary>
public record DescribeTableColumnsCommand : IRequest<DescribeTableColumnsResult>, IProjectAuthorizedRequest
{
    /// <summary>The most columns one request asks the LLM about.</summary>
    public const int MaxColumns = 60;

    public int DataSourceId { get; init; }

    public int ProjectId { get; init; }

    public required string SchemaName { get; init; }

    public required string TableName { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Project(ProjectId), ProjectAction.Curate),
        new(ProjectResource.DataSource(DataSourceId), ProjectAction.Read)
    ];
}

/// <param name="ColumnsAsked">The columns the LLM was asked to describe (0: none needed a description, no call was made).</param>
/// <param name="ProposalsCreated">The proposed column-description patches stored.</param>
public record DescribeTableColumnsResult(int ColumnsAsked, int ProposalsCreated);
