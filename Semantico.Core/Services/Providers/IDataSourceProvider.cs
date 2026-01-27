using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Providers;

namespace Semantico.Core.Services.Providers;

public interface IDataSourceProvider
{
    /// <summary>
    /// The data source type this provider supports
    /// </summary>
    DataSourceType SupportedType { get; }

    /// <summary>
    /// Tests connectivity to the data source
    /// </summary>
    Task<ConnectionTestResult> TestConnectionAsync(
        DataSource dataSource,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a query and returns normalized results
    /// </summary>
    Task<ProviderQueryResult> ExecuteQueryAsync(
        DataSource dataSource,
        string query,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets metadata about the data source (schema, fields, etc.)
    /// </summary>
    Task<DataSourceMetadata> GetMetadataAsync(
        DataSource dataSource,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates query syntax without executing
    /// </summary>
    Task<QueryValidationResult> ValidateQueryAsync(
        DataSource dataSource,
        string query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the query language name for UI display
    /// </summary>
    string GetQueryLanguageName();
}
