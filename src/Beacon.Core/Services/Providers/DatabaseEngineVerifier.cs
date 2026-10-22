using System.Data.Common;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// Checks that a database data source's server is the engine it declares (<see cref="DatabaseEngineIdentity"/>), with one
/// fixed catalog statement Beacon writes, run outside the SQL gate like the metadata extractors' catalog SQL. A server
/// that cannot be reached or whose answer cannot be read is never refused: a Warning names the data source by id only.
/// </summary>
internal interface IDatabaseEngineVerifier
{
    /// <summary>
    /// Identifies the engine on <paramref name="openConnection"/>, already opened with the connection being checked.
    /// Never throws for an answer it cannot read (<see cref="DatabaseEngineIdentityVerdict.Unverified"/>); the caller's
    /// cancellation propagates.
    /// </summary>
    Task<DatabaseEngineIdentification> IdentifyAsync(
        int? dataSourceId,
        DatabaseEngineType declared,
        DbConnection openConnection,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a connection with <paramref name="connectionString"/> (a new connection, after its transport check) and
    /// refuses a clear mismatch with an <see cref="InvalidOperationException"/> naming the declared and detected engine
    /// kinds, never a version or a host, after emptying the driver's pool of that connection string. Connects to nothing
    /// for an engine without an identification statement.
    /// </summary>
    Task EnsureDeclaredEngineAsync(
        int? dataSourceId,
        DatabaseEngineType declared,
        string connectionString,
        CancellationToken cancellationToken);
}

internal sealed class DatabaseEngineVerifier(ILogger<DatabaseEngineVerifier> logger) : IDatabaseEngineVerifier
{
    // How long connecting and identifying may take before the check gives up (and passes, with a Warning).
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<DatabaseEngineIdentification> IdentifyAsync(
        int? dataSourceId,
        DatabaseEngineType declared,
        DbConnection openConnection,
        CancellationToken cancellationToken)
    {
        var sql = DatabaseEngineIdentity.IdentificationSql(declared);
        if (sql == null)
        {
            return DatabaseEngineIdentity.Classify(declared, null);
        }

        object? answer;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            await using var command = openConnection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = (int)Timeout.TotalSeconds;
            answer = await command.ExecuteScalarAsync(timeout.Token);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The type only: a driver's message can name the host (§1.11).
            logger.LogWarning(
                "The engine of data source {DataSourceId} could not be identified ({ExceptionType}); its declared engine {DeclaredEngine} was not verified",
                dataSourceId,
                ex.GetType().Name,
                declared);

            return DatabaseEngineIdentification.Unverified;
        }

        var identification = DatabaseEngineIdentity.Classify(declared, answer == DBNull.Value ? null : answer);
        LogVerdict(dataSourceId, declared, identification);

        return identification;
    }

    public async Task EnsureDeclaredEngineAsync(
        int? dataSourceId,
        DatabaseEngineType declared,
        string connectionString,
        CancellationToken cancellationToken)
    {
        if (DatabaseEngineIdentity.IdentificationSql(declared) == null)
        {
            return;
        }

        DatabaseEngineIdentification identification;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            await using var connection = DbConnectionFactory.CreateConnection(declared, connectionString);
            await connection.OpenAsync(timeout.Token);
            identification = await IdentifyAsync(dataSourceId, declared, connection, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Could not connect to data source {DataSourceId} to identify its engine ({ExceptionType}); its declared engine {DeclaredEngine} was not verified",
                dataSourceId,
                ex.GetType().Name,
                declared);

            return;
        }

        if (identification.Verdict == DatabaseEngineIdentityVerdict.Mismatch)
        {
            // No idle connection opened with the refused credentials stays in the pool.
            ClearPool(dataSourceId, declared, connectionString);

            throw new InvalidOperationException(identification.Message);
        }
    }

    // Empties the driver's pools of a refused connection string. The refusal stands either way, so a failure is logged by its
    // type only, never its message, which can quote the connection string.
    private void ClearPool(int? dataSourceId, DatabaseEngineType engine, string connectionString)
    {
        try
        {
            DbConnectionFactory.ClearPool(engine, connectionString);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "The connection pool of data source {DataSourceId} could not be cleared after its engine was refused ({ExceptionType})",
                dataSourceId,
                ex.GetType().Name);
        }
    }

    private void LogVerdict(int? dataSourceId, DatabaseEngineType declared, DatabaseEngineIdentification identification)
    {
        if (identification.Verdict == DatabaseEngineIdentityVerdict.Mismatch)
        {
            logger.LogWarning(
                "Data source {DataSourceId} declares {DeclaredEngine} but the server is {DetectedEngine}: refused",
                dataSourceId,
                declared,
                identification.DetectedKind);
        }
        else if (identification.Verdict == DatabaseEngineIdentityVerdict.Compatible)
        {
            logger.LogWarning(
                "Data source {DataSourceId} declares {DeclaredEngine} and the server is {DetectedEngine}, a compatible engine Beacon reads as the declared one",
                dataSourceId,
                declared,
                identification.DetectedKind);
        }
        else if (identification.Verdict == DatabaseEngineIdentityVerdict.Unverified)
        {
            logger.LogWarning(
                "The engine of data source {DataSourceId} could not be read from the server's answer; its declared engine {DeclaredEngine} was not verified",
                dataSourceId,
                declared);
        }
    }
}
