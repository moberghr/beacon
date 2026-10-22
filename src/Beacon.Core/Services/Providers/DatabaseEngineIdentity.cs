using System.Globalization;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services.Providers;

/// <summary>What the server's own answer says about the engine a data source declares.</summary>
internal enum DatabaseEngineIdentityVerdict
{
    /// <summary>The answer could not be read (no connection, a failed or empty answer): nothing is refused.</summary>
    Unverified = 0,

    /// <summary>The declared engine has no identification statement (Snowflake, SQLite): nothing is checked.</summary>
    NotChecked,

    /// <summary>The server is the declared engine.</summary>
    Match,

    /// <summary>The server is an engine commonly run as the declared one (MariaDB, TiDB as MySQL): a warning, not a refusal.</summary>
    Compatible,

    /// <summary>The server is another engine than the declared one: refused.</summary>
    Mismatch
}

/// <summary>
/// The verdict on a declared engine, with the engine kind detected (a fixed label from <see cref="DatabaseEngineIdentity"/>,
/// never a version string or a host name); <see cref="Message"/> is what a caller is told on a mismatch or a warning.
/// </summary>
internal readonly record struct DatabaseEngineIdentification(DatabaseEngineIdentityVerdict Verdict, string? DetectedKind, string? Message)
{
    public static DatabaseEngineIdentification Unverified => new(DatabaseEngineIdentityVerdict.Unverified, null, null);
}

/// <summary>
/// Identifies the engine behind a database connection from one fixed catalog statement, for the engines whose wire protocol
/// other products share: PostgreSQL (<c>version()</c>: must start with <c>PostgreSQL</c>, and a known fork's marker is
/// another engine), SQL Server and Azure Synapse (<c>SERVERPROPERTY('EngineEdition')</c>: 6 and 11 are Synapse) and MySQL
/// (<c>VERSION()</c>: MariaDB and TiDB are a warning only). Pure: the statement is run by
/// <see cref="DatabaseEngineVerifier"/>, which passes the answer to <see cref="Classify"/>.
/// </summary>
internal static class DatabaseEngineIdentity
{
    private const string PostgreSqlVersion = "SELECT version()";

    private const string SqlServerEngineEdition = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)";

    private const string MySqlVersion = "SELECT VERSION()";

    // EngineEdition values of an Azure Synapse pool (6: dedicated SQL pool, 11: serverless SQL pool).
    private static readonly int[] SynapseEditions = [6, 11];

    // Products answering version() with a PostgreSQL-looking string: the marker and the kind named to the caller.
    private static readonly (string Marker, string Kind)[] PostgreSqlForks =
    [
        ("Redshift", "Amazon Redshift"),
        ("Greenplum", "Greenplum"),
        ("CockroachDB", "CockroachDB"),
        ("-YB-", "YugabyteDB")
    ];

    // Products commonly run as MySQL: a warning, never a refusal.
    private static readonly (string Marker, string Kind)[] MySqlCompatibles =
    [
        ("MariaDB", "MariaDB"),
        ("TiDB", "TiDB")
    ];

    /// <summary>The fixed statement that identifies <paramref name="declared"/>, or null when it is not checked.</summary>
    public static string? IdentificationSql(DatabaseEngineType declared)
    {
        return declared switch
        {
            DatabaseEngineType.PostgreSQL => PostgreSqlVersion,
            DatabaseEngineType.MSSQL or DatabaseEngineType.AzureSynapse => SqlServerEngineEdition,
            DatabaseEngineType.MySQL => MySqlVersion,
            _ => null
        };
    }

    /// <summary>The verdict on <paramref name="declared"/> from the server's answer to <see cref="IdentificationSql"/>.</summary>
    public static DatabaseEngineIdentification Classify(DatabaseEngineType declared, object? answer)
    {
        if (IdentificationSql(declared) == null)
        {
            return new DatabaseEngineIdentification(DatabaseEngineIdentityVerdict.NotChecked, null, null);
        }

        return declared switch
        {
            DatabaseEngineType.PostgreSQL => ClassifyPostgreSql(Text(answer)),
            DatabaseEngineType.MySQL => ClassifyMySql(Text(answer)),
            _ => ClassifySqlServer(declared, Edition(answer))
        };
    }

    /// <summary>The engine's name as a caller is told it.</summary>
    public static string Label(DatabaseEngineType engine)
    {
        return engine switch
        {
            DatabaseEngineType.PostgreSQL => "PostgreSQL",
            DatabaseEngineType.MSSQL => "SQL Server",
            DatabaseEngineType.MySQL => "MySQL",
            DatabaseEngineType.AzureSynapse => "Azure Synapse",
            _ => engine.ToString()
        };
    }

    private static DatabaseEngineIdentification ClassifyPostgreSql(string? version)
    {
        if (version == null)
        {
            return DatabaseEngineIdentification.Unverified;
        }

        var fork = PostgreSqlForks
            .Where(x => version.Contains(x.Marker, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Kind)
            .FirstOrDefault();
        if (fork != null)
        {
            return Mismatch(DatabaseEngineType.PostgreSQL, fork);
        }

        return version.StartsWith("PostgreSQL", StringComparison.OrdinalIgnoreCase)
            ? new DatabaseEngineIdentification(DatabaseEngineIdentityVerdict.Match, Label(DatabaseEngineType.PostgreSQL), null)
            : Mismatch(DatabaseEngineType.PostgreSQL, "another engine");
    }

    private static DatabaseEngineIdentification ClassifyMySql(string? version)
    {
        if (version == null)
        {
            return DatabaseEngineIdentification.Unverified;
        }

        var compatible = MySqlCompatibles
            .Where(x => version.Contains(x.Marker, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Kind)
            .FirstOrDefault();

        return compatible == null
            ? new DatabaseEngineIdentification(DatabaseEngineIdentityVerdict.Match, Label(DatabaseEngineType.MySQL), null)
            : new DatabaseEngineIdentification(
                DatabaseEngineIdentityVerdict.Compatible,
                compatible,
                $"The server is {compatible}, run as MySQL: Beacon reads it with MySQL's driver, grammar and read-only session, which {compatible} may not honour in full.");
    }

    private static DatabaseEngineIdentification ClassifySqlServer(DatabaseEngineType declared, int? edition)
    {
        if (edition == null)
        {
            return DatabaseEngineIdentification.Unverified;
        }

        var detected = SynapseEditions.Contains(edition.Value) ? DatabaseEngineType.AzureSynapse : DatabaseEngineType.MSSQL;

        return detected == declared
            ? new DatabaseEngineIdentification(DatabaseEngineIdentityVerdict.Match, Label(detected), null)
            : Mismatch(declared, Label(detected));
    }

    private static DatabaseEngineIdentification Mismatch(DatabaseEngineType declared, string detectedKind)
    {
        return new DatabaseEngineIdentification(
            DatabaseEngineIdentityVerdict.Mismatch,
            detectedKind,
            $"The server is {detectedKind}, not the declared engine {Label(declared)}. Choose the engine the server runs: Beacon picks the driver, the SQL grammar and the read-only checks by it.");
    }

    private static string? Text(object? answer)
    {
        return answer is string text && !string.IsNullOrWhiteSpace(text) ? text : null;
    }

    private static int? Edition(object? answer)
    {
        return answer switch
        {
            int value => value,
            long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
            short value => value,
            byte value => value,
            decimal value when value == decimal.Truncate(value) && value is >= int.MinValue and <= int.MaxValue => (int)value,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }
}
