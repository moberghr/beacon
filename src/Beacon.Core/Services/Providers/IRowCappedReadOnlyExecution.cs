using Beacon.Core.Data.Entities;
using Beacon.Core.Models.Providers;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// A provider whose read-only path stops reading a result once it holds a given number of rows, and cancels the rest of
/// the statement, so a statement whose own LIMIT / TOP lies above Beacon's row cap is not fetched in full before it fails.
/// <see cref="GatedSqlRunner"/> uses it when the provider offers it; other providers return the whole result and the
/// runner judges its size afterwards.
/// </summary>
internal interface IRowCappedReadOnlyExecution
{
    /// <summary>
    /// <see cref="IDataSourceProvider.ExecuteReadOnlyQueryAsync"/>, reading at most <paramref name="maxRows"/> rows. A result
    /// that holds exactly <paramref name="maxRows"/> rows may have had more.
    /// </summary>
    Task<ProviderQueryResult> ExecuteReadOnlyQueryAsync(
        DataSource dataSource,
        string query,
        Dictionary<string, object?> parameters,
        int maxRows,
        CancellationToken cancellationToken);
}
