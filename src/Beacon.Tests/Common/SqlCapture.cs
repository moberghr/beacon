using System.Data;
using System.Data.Common;
using Beacon.Core.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Npgsql;

namespace Beacon.Tests.Common;

/// <summary>Thrown by <see cref="SqlCapture"/> when a command has no scripted answer: the SQL was captured, nothing ran.</summary>
internal sealed class SqlCapturedException() : Exception("The command was captured and not executed.");

/// <summary>
/// Runs real service code against a <see cref="BeaconContext"/> on the Npgsql provider without a database (§4.7):
/// opening a connection is suppressed, every command's SQL is captured and answered from a script (or stopped with
/// <see cref="SqlCapturedException"/>), and every transaction start records its isolation level and gets a stand-in.
/// No connection is ever opened, so nothing is created or dropped. <see cref="OnSqlServer"/> does the same on the SQL
/// Server provider, for the SQL Server twin of a translation test.
/// </summary>
internal sealed class SqlCapture
{
    private readonly bool _sqlServer;

    private readonly Queue<(Func<DbDataReader> Reader, int RowsAffected)> _answers = new();
    private readonly List<string> _commands = [];
    private readonly List<IReadOnlyDictionary<string, object?>> _parameters = [];
    private readonly List<IsolationLevel> _transactions = [];
    private Func<string, (Func<DbDataReader> Reader, int RowsAffected)>? _fallback;

    public SqlCapture()
    {
    }

    private SqlCapture(bool sqlServer)
    {
        _sqlServer = sqlServer;
    }

    /// <summary>A capture on the SQL Server provider (<see cref="SqlServerTestContext"/>) instead of Npgsql.</summary>
    public static SqlCapture OnSqlServer()
    {
        return new SqlCapture(sqlServer: true);
    }

    public IReadOnlyList<string> Commands => _commands;

    /// <summary>The parameters each command in <see cref="Commands"/> was sent with, by name.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> CommandParameters => _parameters;

    public IReadOnlyList<IsolationLevel> TransactionIsolationLevels => _transactions;

    /// <summary>How many of the stand-in transactions were committed (the others were rolled back or disposed).</summary>
    public int Commits { get; private set; }

    /// <summary>
    /// Answers every command the script does not: a <c>count(*)</c> read with one, anything else with no rows. For tests
    /// that compare the SQL a handler sends, not what it does with the rows.
    /// </summary>
    public SqlCapture AnsweringCountsWithOne()
    {
        _fallback = sql => sql.Contains("count(*)", StringComparison.OrdinalIgnoreCase)
            && sql.TrimStart().StartsWith("SELECT count(", StringComparison.OrdinalIgnoreCase)
                ? (() => Scalar(1), 1)
                : (() => new DataTable().CreateDataReader(), 1);

        return this;
    }

    /// <summary>Answers the next command with no rows.</summary>
    public SqlCapture ThenNoRows()
    {
        _answers.Enqueue((() => new DataTable().CreateDataReader(), 1));

        return this;
    }

    /// <summary>
    /// Answers the next command with one row holding <paramref name="values"/>, in column order; <see cref="DBNull.Value"/>
    /// stands for a null column.
    /// </summary>
    public SqlCapture ThenRow(params object[] values)
    {
        _answers.Enqueue((() =>
        {
            var table = new DataTable();
            for (var i = 0; i < values.Length; i++)
            {
                table.Columns.Add($"c{i}", ColumnTypeOf(values[i]));
            }

            table.Rows.Add(values);

            return table.CreateDataReader();
        }, 1));

        return this;
    }

    /// <summary>Answers the next command with <paramref name="rows"/>, each holding its values in column order.</summary>
    public SqlCapture ThenRows(params object[][] rows)
    {
        if (rows.Length == 0)
        {
            return ThenNoRows();
        }

        _answers.Enqueue((() =>
        {
            var table = new DataTable();
            for (var i = 0; i < rows[0].Length; i++)
            {
                table.Columns.Add($"c{i}", ColumnTypeOf(rows[0][i]));
            }

            foreach (var row in rows)
            {
                table.Rows.Add(row);
            }

            return table.CreateDataReader();
        }, rows.Length));

        return this;
    }

    /// <summary>Answers the next command, a write such as <c>ExecuteUpdateAsync</c>, as having changed <paramref name="rows"/> rows.</summary>
    public SqlCapture ThenRowsAffected(int rows)
    {
        _answers.Enqueue((() => new DataTable().CreateDataReader(), rows));

        return this;
    }

    /// <summary>Answers the next command by throwing <paramref name="exception"/>, as the database would on a failed write.</summary>
    public SqlCapture ThenThrow(Exception exception)
    {
        _answers.Enqueue((() => throw exception, 0));

        return this;
    }

    /// <summary>Answers the next command with one row holding <paramref name="value"/>.</summary>
    public SqlCapture ThenScalar(object value)
    {
        _answers.Enqueue((() =>
        {
            var table = new DataTable();
            table.Columns.Add("value", value.GetType());
            table.Rows.Add(value);

            return table.CreateDataReader();
        }, 1));

        return this;
    }

    // A null column is read through IsDBNull, whatever its type.
    private static Type ColumnTypeOf(object value)
    {
        return value is DBNull ? typeof(string) : value.GetType();
    }

    private static DbDataReader Scalar(object value)
    {
        var table = new DataTable();
        table.Columns.Add("value", value.GetType());
        table.Rows.Add(value);

        return table.CreateDataReader();
    }

    /// <summary>
    /// For a context that must never reach a database server (§4.7) — the translation contexts, the recording context's
    /// unserved sets: opening its connection is suppressed, so a command it runs by mistake fails at once with "connection
    /// is not open" instead of connecting to whatever listens on localhost (and a test never waits on a port that accepts
    /// but does not answer).
    /// </summary>
    internal static IInterceptor NeverConnects { get; } = new NoConnection();

    /// <summary>
    /// The interceptors that keep a context offline without capturing its commands: opening a connection is suppressed and
    /// transactions are stand-ins recorded in <see cref="TransactionIsolationLevels"/>.
    /// </summary>
    internal IInterceptor[] OfflineInterceptors()
    {
        return [new NoConnection(), new StandInTransactions(this)];
    }

    public IDbContextFactory<BeaconContext> Factory()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateContext());
        factory
            .Setup(x => x.CreateDbContext())
            .Returns(() => CreateContext());

        return factory.Object;
    }

    private BeaconContext CreateContext()
    {
        if (_sqlServer)
        {
            var sqlServerOptions = new DbContextOptionsBuilder<SqlServerTestContext>()
                .UseSqlServer("Server=localhost;Database=never_opened;TrustServerCertificate=True")
                .AddInterceptors(new NoConnection(), new CapturingCommands(this), new StandInTransactions(this))
                .Options;

            return new SqlServerTestContext(sqlServerOptions);
        }

        var options = new DbContextOptionsBuilder<NpgsqlTestContext>()
            .UseNpgsql("Host=localhost;Database=never_opened")
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new NoConnection(), new CapturingCommands(this), new StandInTransactions(this))
            .Options;

        return new NpgsqlTestContext(options);
    }

    private (DbDataReader Reader, int RowsAffected) Answer(string sql, DbParameterCollection parameters)
    {
        lock (_commands)
        {
            _commands.Add(sql);
            _parameters.Add(parameters
                .Cast<DbParameter>()
                .ToDictionary(x => x.ParameterName, x => x.Value == DBNull.Value ? null : x.Value));
            if (_answers.Count == 0 && _fallback != null)
            {
                var fallback = _fallback(sql);

                return (fallback.Reader(), fallback.RowsAffected);
            }

            if (_answers.Count == 0)
            {
                throw new SqlCapturedException();
            }

            var answer = _answers.Dequeue();

            return (answer.Reader(), answer.RowsAffected);
        }
    }

    private sealed class NoConnection : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result) => InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(InterceptionResult.Suppress());

        public override InterceptionResult ConnectionClosing(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result) => InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionClosingAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result) => ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class CapturingCommands(SqlCapture capture) : DbCommandInterceptor
    {
        public override InterceptionResult<DbCommand> CommandCreating(
            CommandCorrelatedEventData eventData,
            InterceptionResult<DbCommand> result) => InterceptionResult<DbCommand>.SuppressWithResult(new CapturingCommand(capture, capture._sqlServer));
    }

    private sealed class StandInTransactions(SqlCapture capture) : DbTransactionInterceptor
    {
        public override InterceptionResult<DbTransaction> TransactionStarting(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result)
        {
            capture._transactions.Add(eventData.IsolationLevel);

            return InterceptionResult<DbTransaction>.SuppressWithResult(new StandInTransaction(capture, connection, eventData.IsolationLevel));
        }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(TransactionStarting(connection, eventData, result));
        }
    }

    private sealed class StandInTransaction(SqlCapture capture, DbConnection connection, IsolationLevel isolationLevel) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => isolationLevel;

        protected override DbConnection DbConnection => connection;

        public override void Commit()
        {
            capture.Commits++;
        }

        public override void Rollback()
        {
        }
    }

    /// <summary>
    /// A command that records its SQL and answers from the script; parameters are real parameters of the provider (Npgsql,
    /// or SQL Server's on <paramref name="sqlServer"/>), as its type mappings expect.
    /// </summary>
    private sealed class CapturingCommand(SqlCapture capture, bool sqlServer) : DbCommand
    {
        private readonly DbParameterCollection _parameters = sqlServer ? new SqlCommand().Parameters : new NpgsqlCommand().Parameters;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => _parameters;

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery()
        {
            var (reader, rowsAffected) = capture.Answer(CommandText, Parameters);
            reader.Dispose();

            return rowsAffected;
        }

        public override object? ExecuteScalar()
        {
            using var reader = capture.Answer(CommandText, Parameters).Reader;

            return reader.Read() ? reader.GetValue(0) : null;
        }

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => sqlServer ? new SqlParameter() : new NpgsqlParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => capture.Answer(CommandText, Parameters).Reader;
    }
}
