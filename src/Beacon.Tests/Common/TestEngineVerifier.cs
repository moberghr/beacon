using System.Data.Common;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Tests.Common;

/// <summary>
/// A declared-engine check that connects to nothing: every engine passes unverified, and each call is counted. For tests of
/// code that saves or tests data sources without a server behind them.
/// </summary>
internal sealed class TestEngineVerifier : IDatabaseEngineVerifier
{
    public static TestEngineVerifier Passing => new();

    public int Calls { get; private set; }

    public Task<DatabaseEngineIdentification> IdentifyAsync(
        int? dataSourceId,
        DatabaseEngineType declared,
        DbConnection openConnection,
        CancellationToken cancellationToken)
    {
        Calls++;

        return Task.FromResult(DatabaseEngineIdentification.Unverified);
    }

    public Task EnsureDeclaredEngineAsync(
        int? dataSourceId,
        DatabaseEngineType declared,
        string connectionString,
        CancellationToken cancellationToken)
    {
        Calls++;

        return Task.CompletedTask;
    }
}
