using System.Data;
using System.Data.Common;
using Beacon.Core.Data;
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
/// No connection is ever opened, so nothing is created or dropped.
/// </summary>
internal sealed class SqlCapture
{
    private readonly Queue<(Func<DbDataReader> Reader, int RowsAffected)> _answers = new();
    private readonly List<string> _commands = [];
    private readonly List<IReadOnlyDictionary<string, object?>> _parameters = [];
    private readonly List<IsolationLevel> _transactions = [];

    public IReadOnlyList<string> Commands => _commands;

    /// <summary>The parameters each command in <see cref="Commands"/> was sent with, by name.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> CommandParameters => _parameters;

    public IReadOnlyList<IsolationLevel> TransactionIsolationLevels => _transactions;

    /// <summary>Answers the next command with no rows.</summary>
    public SqlCapture ThenNoRows()
    {
        _answers.Enqueue((() => new DataTable().CreateDataReader(), 1));

        return this;
    }

    /// <summary>Answers the next command with one row holding <paramref name="values"/>, in column order.</summary>
    public SqlCapture ThenRow(params object[] values)
    {
        _answers.Enqueue((() =>
        {
            var table = new DataTable();
            for (var i = 0; i < values.Length; i++)
            {
                table.Columns.Add($"c{i}", values[i].GetType());
            }

            table.Rows.Add(values);

            return table.CreateDataReader();
        }, 1));

        return this;
    }

    /// <summary>Answers the next command, a write such as <c>ExecuteUpdateAsync</c>, as having changed <paramref name="rows"/> rows.</summary>
    public SqlCapture ThenRowsAffected(int rows)
    {
        _answers.Enqueue((() => new DataTable().CreateDataReader(), rows));

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

    private NpgsqlTestContext CreateContext()
    {
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
            InterceptionResult<DbCommand> result) => InterceptionResult<DbCommand>.SuppressWithResult(new CapturingCommand(capture));
    }

    private sealed class StandInTransactions(SqlCapture capture) : DbTransactionInterceptor
    {
        public override InterceptionResult<DbTransaction> TransactionStarting(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result)
        {
            capture._transactions.Add(eventData.IsolationLevel);

            return InterceptionResult<DbTransaction>.SuppressWithResult(new StandInTransaction(connection, eventData.IsolationLevel));
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

    private sealed class StandInTransaction(DbConnection connection, IsolationLevel isolationLevel) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => isolationLevel;

        protected override DbConnection DbConnection => connection;

        public override void Commit()
        {
        }

        public override void Rollback()
        {
        }
    }

    /// <summary>A command that records its SQL and answers from the script; parameters are real Npgsql parameters.</summary>
    private sealed class CapturingCommand(SqlCapture capture) : DbCommand
    {
        private readonly NpgsqlCommand _parameters = new();

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => _parameters.Parameters;

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

        protected override DbParameter CreateDbParameter() => new NpgsqlParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => capture.Answer(CommandText, Parameters).Reader;
    }
}
