using Beacon.Core.Data.Entities;
using Beacon.Core.Models.Providers;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// An <see cref="IDataSourceProvider"/> whose read-only path can stop reading a result once it holds a given number of
/// rows, so a statement whose own LIMIT / TOP lies above Beacon's row cap is not fetched in full before it fails.
/// <see cref="GatedSqlRunner"/> uses it when the provider offers it, passing one row more than the caller's cap; other
/// providers return the whole result and the runner judges its size afterwards. A connector implements it when its engine
/// or driver lets it bound the read (a reader closed early, a warehouse row limit); it must never return fewer rows than
/// the statement produced unless it returns exactly <c>maxRows</c>, or fail.
/// </summary>
public interface IRowCappedReadOnlyExecution
{
    /// <summary>
    /// <see cref="IDataSourceProvider.ExecuteReadOnlyQueryAsync"/>, with the same read-only guarantees, reading at most
    /// <paramref name="maxRows"/> rows. A result that holds exactly <paramref name="maxRows"/> rows may have had more, and
    /// the caller treats it as too large.
    /// </summary>
    Task<ProviderQueryResult> ExecuteReadOnlyQueryAsync(
        DataSource dataSource,
        string query,
        Dictionary<string, object?> parameters,
        int maxRows,
        CancellationToken cancellationToken);
}
