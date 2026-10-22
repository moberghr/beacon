using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Services.Providers;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Beacon.Tests.Unit.EngineVerification;

/// <summary>
/// The declared engine of a database data source against the server's own answer: a clear mismatch is refused, a
/// MySQL-compatible engine is a warning, an answer that cannot be read passes unverified, and the engines without an
/// identification statement are not checked. What a caller is told names engine kinds only, never a version string.
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class DatabaseEngineIdentityTests
{
    private static readonly object?[][] Answers =
    [
        [DatabaseEngineType.PostgreSQL, "PostgreSQL 16.2 on x86_64-pc-linux-gnu, compiled by gcc 12.2.0, 64-bit", "Match", "PostgreSQL"],
        [DatabaseEngineType.PostgreSQL, "PostgreSQL 8.0.2 on i686-pc-linux-gnu, compiled by GCC gcc (GCC) 3.4.2 20041017 (Red Hat 3.4.2-6.fc3), Redshift 1.0.77467", "Mismatch", "Amazon Redshift"],
        [DatabaseEngineType.PostgreSQL, "PostgreSQL 9.4.26 (Greenplum Database 6.24.3 build commit:abc) on x86_64-unknown-linux-gnu", "Mismatch", "Greenplum"],
        [DatabaseEngineType.PostgreSQL, "CockroachDB CCL v23.1.11 (x86_64-pc-linux-gnu, built 2023/09/27 01:53:43, go1.19.10)", "Mismatch", "CockroachDB"],
        [DatabaseEngineType.PostgreSQL, "PostgreSQL 11.2-YB-2.20.1.0-b0 on x86_64-pc-linux-gnu, compiled by clang version 16.0.6", "Mismatch", "YugabyteDB"],
        [DatabaseEngineType.PostgreSQL, "8.0.36", "Mismatch", "another engine"],
        // Managed PostgreSQL services and extensions answer as PostgreSQL.
        [DatabaseEngineType.PostgreSQL, "PostgreSQL 15.4 on aarch64-unknown-linux-gnu, compiled by aarch64-unknown-linux-gnu-gcc (GCC) 9.5.0, 64-bit", "Match", "PostgreSQL"],
        [DatabaseEngineType.PostgreSQL, "PostgreSQL 16.4 on x86_64-pc-linux-gnu, compiled by gcc (GCC) 11.2.0, 64-bit", "Match", "PostgreSQL"],
        [DatabaseEngineType.PostgreSQL, "PostgreSQL 11.18, compiled by Visual C++ build 1800, 64-bit", "Match", "PostgreSQL"],
        [DatabaseEngineType.PostgreSQL, "PostgreSQL 15.1 (Ubuntu 15.1-1.pgdg20.04+1) on aarch64-unknown-linux-gnu, compiled by gcc (Ubuntu 9.4.0-1ubuntu1~20.04.1) 9.4.0, 64-bit", "Match", "PostgreSQL"],
        [DatabaseEngineType.PostgreSQL, "PostgreSQL 16.4 on x86_64-pc-linux-gnu, compiled by gcc (Debian 12.2.0-14) 12.2.0, 64-bit", "Match", "PostgreSQL"],
        [DatabaseEngineType.PostgreSQL, "PostgreSQL 16.3 (Ubuntu 16.3-1.pgdg22.04+1) on x86_64-pc-linux-gnu, compiled by gcc (Ubuntu 11.4.0-1ubuntu1~22.04) 11.4.0, 64-bit", "Match", "PostgreSQL"],
        [DatabaseEngineType.PostgreSQL, "PostgreSQL 14.10 on x86_64-pc-linux-gnu, compiled by Debian clang version 12.0.1, 64-bit", "Match", "PostgreSQL"],
        [DatabaseEngineType.PostgreSQL, "PostgreSQL 16.1 (EnterpriseDB Advanced Server 16.1.0) on x86_64-pc-linux-gnu, compiled by gcc (GCC) 8.5.0 20210514 (Red Hat 8.5.0-18), 64-bit", "Match", "PostgreSQL"],
        [DatabaseEngineType.PostgreSQL, "postgresql 16.2 on x86_64-pc-linux-gnu", "Match", "PostgreSQL"],
        [DatabaseEngineType.PostgreSQL, "postgresql 8.0.2 on i686-pc-linux-gnu, redshift 1.0.77467", "Mismatch", "Amazon Redshift"],
        [DatabaseEngineType.PostgreSQL, "postgresql 11.2-yb-2.20.1.0-b0 on x86_64-pc-linux-gnu", "Mismatch", "YugabyteDB"],
        [DatabaseEngineType.PostgreSQL, "cockroachdb ccl v23.1.11", "Mismatch", "CockroachDB"],
        [DatabaseEngineType.PostgreSQL, null, "Unverified", null],
        [DatabaseEngineType.PostgreSQL, "   ", "Unverified", null],
        [DatabaseEngineType.PostgreSQL, 42, "Unverified", null],
        [DatabaseEngineType.MSSQL, 3, "Match", "SQL Server"],
        [DatabaseEngineType.MSSQL, 5, "Match", "SQL Server"],
        [DatabaseEngineType.MSSQL, 8, "Match", "SQL Server"],
        // Express, Azure SQL Edge and Fabric SQL database run the SQL Server engine.
        [DatabaseEngineType.MSSQL, 4, "Match", "SQL Server"],
        [DatabaseEngineType.MSSQL, 9, "Match", "SQL Server"],
        [DatabaseEngineType.MSSQL, 12, "Match", "SQL Server"],
        [DatabaseEngineType.AzureSynapse, 4, "Mismatch", "SQL Server"],
        [DatabaseEngineType.AzureSynapse, 12, "Mismatch", "SQL Server"],
        [DatabaseEngineType.MSSQL, 6, "Mismatch", "Azure Synapse"],
        [DatabaseEngineType.MSSQL, 11, "Mismatch", "Azure Synapse"],
        [DatabaseEngineType.MSSQL, 11L, "Mismatch", "Azure Synapse"],
        [DatabaseEngineType.MSSQL, null, "Unverified", null],
        [DatabaseEngineType.MSSQL, "not a number", "Unverified", null],
        [DatabaseEngineType.AzureSynapse, 6, "Match", "Azure Synapse"],
        [DatabaseEngineType.AzureSynapse, 11, "Match", "Azure Synapse"],
        [DatabaseEngineType.AzureSynapse, 2, "Mismatch", "SQL Server"],
        [DatabaseEngineType.MySQL, "8.0.36", "Match", "MySQL"],
        [DatabaseEngineType.MySQL, "8.0.36-0ubuntu0.22.04.1", "Match", "MySQL"],
        [DatabaseEngineType.MySQL, "10.11.6-MariaDB-0+deb12u1", "Compatible", "MariaDB"],
        [DatabaseEngineType.MySQL, "8.0.11-TiDB-v7.5.0", "Compatible", "TiDB"],
        [DatabaseEngineType.MySQL, "8.0.mysql_aurora.3.04.0", "Match", "MySQL"],
        [DatabaseEngineType.MySQL, "8.0.35-27", "Match", "MySQL"],
        [DatabaseEngineType.MySQL, "10.11.6-mariadb-0+deb12u1", "Compatible", "MariaDB"],
        [DatabaseEngineType.MySQL, "8.0.11-tidb-v7.5.0", "Compatible", "TiDB"],
        [DatabaseEngineType.MySQL, null, "Unverified", null],
        [DatabaseEngineType.Snowflake, "anything", "NotChecked", null],
        [DatabaseEngineType.SQLite, "3.45.1", "NotChecked", null]
    ];

    [TearDown]
    public void RestoreConnectionFactories()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddMySqlConnector();
    }

    [TestCaseSource(nameof(Answers))]
    public void Classify_NamesTheVerdictAndTheDetectedKind(
        DatabaseEngineType declared,
        object? answer,
        string verdict,
        string? detectedKind)
    {
        var identification = DatabaseEngineIdentity.Classify(declared, answer);

        identification.Verdict.ToString().Should().Be(verdict);
        identification.DetectedKind.Should().Be(detectedKind);
    }

    [Test]
    public void Mismatch_NamesTheDeclaredAndDetectedKinds_NeverTheVersionString()
    {
        const string version = "PostgreSQL 8.0.2 on i686-pc-linux-gnu, Redshift 1.0.77467";

        var identification = DatabaseEngineIdentity.Classify(DatabaseEngineType.PostgreSQL, version);

        identification.Message.Should().Contain("Amazon Redshift").And.Contain("PostgreSQL").And.NotContain("1.0.77467").And.NotContain("i686");
    }

    [TestCase(DatabaseEngineType.PostgreSQL, "SELECT version()")]
    [TestCase(DatabaseEngineType.MSSQL, "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)")]
    [TestCase(DatabaseEngineType.AzureSynapse, "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)")]
    [TestCase(DatabaseEngineType.MySQL, "SELECT VERSION()")]
    [TestCase(DatabaseEngineType.Snowflake, null)]
    [TestCase(DatabaseEngineType.SQLite, null)]
    public void IdentificationSql_IsAFixedStatementPerEngine(DatabaseEngineType engine, string? sql)
    {
        DatabaseEngineIdentity.IdentificationSql(engine).Should().Be(sql);
    }

    [Test]
    public async Task Identify_RunsTheFixedStatementOnTheOpenConnection()
    {
        var connection = new RecordingDbConnection { Scalar = _ => "CockroachDB CCL v23.1.11" };
        connection.Open();

        var identification = await Verifier().IdentifyAsync(7, DatabaseEngineType.PostgreSQL, connection, CancellationToken.None);

        identification.Verdict.Should().Be(DatabaseEngineIdentityVerdict.Mismatch);
        connection.Executions.Should().ContainSingle().Which.CommandText.Should().Be("SELECT version()");
    }

    [Test]
    public async Task Identify_WhenTheStatementFails_PassesUnverified_AndWarnsWithTheIdOnly()
    {
        var logs = new LogRecorder();
        var connection = new RecordingDbConnection { FailOnCommandText = _ => true };
        connection.Open();

        var identification = await new DatabaseEngineVerifier(logs.For<DatabaseEngineVerifier>())
            .IdentifyAsync(7, DatabaseEngineType.MSSQL, connection, CancellationToken.None);

        identification.Verdict.Should().Be(DatabaseEngineIdentityVerdict.Unverified);
        logs.Entries.Should().ContainSingle(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
            .Which.Message.Should().Contain("7").And.NotContain("The command failed on the server");
    }

    [Test]
    public async Task Identify_ForAnEngineWithoutAStatement_RunsNothing()
    {
        var connection = new RecordingDbConnection();
        connection.Open();

        var identification = await Verifier().IdentifyAsync(7, DatabaseEngineType.Snowflake, connection, CancellationToken.None);

        identification.Verdict.Should().Be(DatabaseEngineIdentityVerdict.NotChecked);
        connection.Executions.Should().BeEmpty();
    }

    [Test]
    public async Task EnsureDeclaredEngine_OnAClearMismatch_Refuses()
    {
        DbConnectionFactory.Register(DatabaseEngineType.MSSQL, _ => new RecordingDbConnection { Scalar = _ => 6 });

        var act = () => Verifier().EnsureDeclaredEngineAsync(null, DatabaseEngineType.MSSQL, "Server=warehouse", CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("Azure Synapse").And.Contain("SQL Server").And.NotContain("warehouse");
    }

    [Test]
    public async Task EnsureDeclaredEngine_OnAMatch_OrACompatibleEngine_Passes()
    {
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ => new RecordingDbConnection { Scalar = _ => "PostgreSQL 16.2 on x86_64" });
        DbConnectionFactory.Register(DatabaseEngineType.MySQL, _ => new RecordingDbConnection { Scalar = _ => "10.11.6-MariaDB" });

        var postgres = () => Verifier().EnsureDeclaredEngineAsync(null, DatabaseEngineType.PostgreSQL, "Host=db", CancellationToken.None);
        var mariaDb = () => Verifier().EnsureDeclaredEngineAsync(null, DatabaseEngineType.MySQL, "Server=db", CancellationToken.None);

        await postgres.Should().NotThrowAsync();
        await mariaDb.Should().NotThrowAsync("MariaDB run as MySQL is a warning, not a refusal");
    }

    [Test]
    public async Task EnsureDeclaredEngine_WhenTheServerCannotBeReached_Passes_AndWarns()
    {
        var logs = new LogRecorder();
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ => throw new TimeoutException("db.internal did not answer"));

        var act = () => new DatabaseEngineVerifier(logs.For<DatabaseEngineVerifier>())
            .EnsureDeclaredEngineAsync(9, DatabaseEngineType.PostgreSQL, "Host=db.internal", CancellationToken.None);

        await act.Should().NotThrowAsync();
        logs.Entries.Should().ContainSingle(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
            .Which.FullText.Should().Contain("9").And.NotContain("db.internal");
    }

    [Test]
    public async Task Identify_WithACancelledToken_PropagatesTheCancellation()
    {
        var connection = new RecordingDbConnection { Scalar = _ => "PostgreSQL 16.2" };
        connection.Open();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => Verifier().IdentifyAsync(7, DatabaseEngineType.PostgreSQL, connection, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task EnsureDeclaredEngine_WithACancelledToken_PropagatesTheCancellation()
    {
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ => new RecordingDbConnection { Scalar = _ => "PostgreSQL 16.2" });
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => Verifier().EnsureDeclaredEngineAsync(7, DatabaseEngineType.PostgreSQL, "Host=db", cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task Identify_ANullAnswer_PassesUnverified()
    {
        var connection = new RecordingDbConnection { Scalar = _ => DBNull.Value };
        connection.Open();

        var identification = await Verifier().IdentifyAsync(7, DatabaseEngineType.MSSQL, connection, CancellationToken.None);

        identification.Verdict.Should().Be(DatabaseEngineIdentityVerdict.Unverified);
    }

    [TestCase(DatabaseEngineType.PostgreSQL, "PostgreSQL 8.0.2 on i686-pc-linux-gnu, Redshift 1.0.77467")]
    [TestCase(DatabaseEngineType.MySQL, "10.11.6-MariaDB-0+deb12u1")]
    public async Task Identify_LogsTheEngineKinds_NeverTheVersionText(DatabaseEngineType declared, string version)
    {
        var logs = new LogRecorder();
        var connection = new RecordingDbConnection { Scalar = _ => version };
        connection.Open();

        await new DatabaseEngineVerifier(logs.For<DatabaseEngineVerifier>()).IdentifyAsync(7, declared, connection, CancellationToken.None);

        var warning = logs.Entries.Should().ContainSingle(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Warning).Subject;
        warning.FullText.Should().Contain("7").And.NotContainAny("8.0.2", "1.0.77467", "i686", "10.11.6", "deb12u1");
    }

    [Test]
    public async Task EnsureDeclaredEngine_OnAClearMismatch_EmptiesThePoolOfThatConnection()
    {
        var cleared = new List<string>();
        DbConnectionFactory.Register(
            DatabaseEngineType.MSSQL,
            x => new RecordingDbConnection { Scalar = _ => 6, ConnectionString = x },
            x => cleared.Add(x.ConnectionString));

        var act = () => Verifier().EnsureDeclaredEngineAsync(null, DatabaseEngineType.MSSQL, "Server=warehouse;Password=entered", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        cleared.Should().Equal("Server=warehouse;Password=entered");
    }

    [Test]
    public async Task EnsureDeclaredEngine_OnAMatch_KeepsThePool()
    {
        var cleared = new List<string>();
        DbConnectionFactory.Register(
            DatabaseEngineType.MSSQL,
            _ => new RecordingDbConnection { Scalar = _ => 3 },
            x => cleared.Add(x.ConnectionString));

        await Verifier().EnsureDeclaredEngineAsync(null, DatabaseEngineType.MSSQL, "Server=warehouse", CancellationToken.None);

        cleared.Should().BeEmpty();
    }

    private static DatabaseEngineVerifier Verifier()
    {
        return new DatabaseEngineVerifier(Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseEngineVerifier>.Instance);
    }
}
