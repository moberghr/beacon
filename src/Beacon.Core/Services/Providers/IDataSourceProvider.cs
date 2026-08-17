using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Providers;

namespace Beacon.Core.Services.Providers;

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
    /// Executes a query with a database-level read-only guarantee where the engine supports one.
    /// Defaults to the normal execution path; providers override to enforce read-only at the
    /// session/transaction level (§1.5 backstop — parser-level enforcement alone is bypassable).
    /// Check <see cref="SupportsDatabaseReadOnlyEnforcement"/> for whether the guarantee is real
    /// for a given engine.
    /// </summary>
    Task<ProviderQueryResult> ExecuteReadOnlyQueryAsync(
        DataSource dataSource,
        string query,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default) =>
        ExecuteQueryAsync(dataSource, query, parameters, cancellationToken);

    /// <summary>
    /// True when <see cref="ExecuteReadOnlyQueryAsync"/> provides a database/session-level
    /// read-only guarantee for the given engine rather than forwarding to normal execution.
    /// The default is false — the base <see cref="ExecuteReadOnlyQueryAsync"/> just forwards to
    /// <see cref="ExecuteQueryAsync"/>, so callers relying on a §1.5 database-level backstop must
    /// not assume one exists unless this reports true.
    /// </summary>
    bool SupportsDatabaseReadOnlyEnforcement(DatabaseEngineType? engine) => false;

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
