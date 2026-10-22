using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Beacon.Tests.Common;

/// <summary>
/// An ADO.NET connection double for code that opens connections through <c>DbConnectionFactory</c>: it records every
/// command it is asked to run (with the transaction it was enlisted in) — a batch's commands too, with the batch's size in
/// <see cref="BatchSizes"/> — counts transactions begun, and answers every reader with the same <see cref="RowCount"/>
/// rows of <see cref="Columns"/>, counting each row a caller reads. No database is involved (§4.7).
/// </summary>
internal sealed class RecordingDbConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;

    /// <summary>The result columns every reader returns, with the value of each row (by its zero-based index).</summary>
    public IReadOnlyList<(string Name, Func<int, object?> Value)> Columns { get; init; } = [("id", x => x + 1)];

    /// <summary>The result columns of a reader by its command text, in place of <see cref="Columns"/>; null keeps them.</summary>
    public Func<string, IReadOnlyList<(string Name, Func<int, object?> Value)>?>? ColumnsFor { get; init; }

    public int RowCount { get; init; }

    /// <summary>Answers a scalar command (a count); the default is <see cref="RowCount"/>.</summary>
    public Func<string, object?>? Scalar { get; init; }

    public List<(string CommandText, DbTransaction? Transaction, IReadOnlyList<DbParameter> Parameters)> Executions { get; } = [];

    /// <summary>The timeout of every command recorded by <see cref="Record"/>, in order.</summary>
    public List<int> CommandTimeouts { get; } = [];

    public int BeginTransactionCalls { get; private set; }

    public int RowsRead { get; set; }

    /// <summary>How many times a command was cancelled (a reader closed early).</summary>
    public int CancelCalls { get; private set; }

    /// <summary>How many transactions were committed.</summary>
    public int Commits { get; private set; }

    /// <summary>How many transactions were rolled back.</summary>
    public int Rollbacks { get; private set; }

    /// <summary>
    /// The zero-based row at which reading fails as a lost connection would; null reads every row. A row before it is read
    /// and counted.
    /// </summary>
    public int? FailReadingAtRow { get; init; }

    /// <summary>
    /// What closing a reader throws the first time — the driver reporting the cancellation of a statement that was stopped
    /// early, say; null closes quietly.
    /// </summary>
    public Exception? FailClosingReaderWith { get; init; }

    /// <summary>What committing a transaction throws, as a connection lost during the commit would; null commits.</summary>
    public Exception? FailCommitWith { get; init; }

    /// <summary>The number of commands of every batch run, in order.</summary>
    public List<int> BatchSizes { get; } = [];

    /// <summary>The text of every command run as part of a batch, in order.</summary>
    public List<string> BatchedCommands { get; } = [];

    public IReadOnlyList<string> ExecutedCommands => Executions
        .Select(x => x.CommandText)
        .ToList();

    [AllowNull]
    public override string ConnectionString { get; set; } = "";

    public override string Database => "unused";

    public override string DataSource => "unused";

    public override string ServerVersion => "0.0";

    public override ConnectionState State => _state;

    /// <summary>A command whose text this matches is recorded, then fails as a server error would.</summary>
    public Func<string, bool>? FailOnCommandText { get; init; }

    public void Record(DbCommand command)
    {
        Executions.Add((command.CommandText, command.Transaction, command.Parameters.Cast<DbParameter>().ToList()));
        CommandTimeouts.Add(command.CommandTimeout);
        if (FailOnCommandText?.Invoke(command.CommandText) == true)
        {
            throw new InvalidOperationException("The command failed on the server.");
        }
    }

    public override void Open() => _state = ConnectionState.Open;

    public override void Close() => _state = ConnectionState.Closed;

    public override void ChangeDatabase(string databaseName)
    {
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        BeginTransactionCalls++;

        return new Transaction(this);
    }

    protected override DbCommand CreateDbCommand() => new Command(this);

    public override bool CanCreateBatch => true;

    protected override DbBatch CreateDbBatch() => new Batch(this);

    private sealed class Batch(RecordingDbConnection connection) : DbBatch
    {
        private readonly BatchCommands _commands = new();

        public override int Timeout { get; set; }

        protected override DbBatchCommandCollection DbBatchCommands => _commands;

        protected override DbConnection? DbConnection { get; set; } = connection;

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
            connection.CancelCalls++;
        }

        public override int ExecuteNonQuery()
        {
            Record();

            return 0;
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default) => Task.FromResult(ExecuteNonQuery());

        public override object? ExecuteScalar()
        {
            Record();

            return connection.RowCount;
        }

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default) => Task.FromResult(ExecuteScalar());

        public override void Prepare()
        {
        }

        public override Task PrepareAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        protected override DbBatchCommand CreateDbBatchCommand() => new BatchCommand();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Record();

            return new Reader(connection, connection.ColumnsOf(_commands.Items.LastOrDefault()?.CommandText));
        }

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
            Task.FromResult(ExecuteDbDataReader(behavior));

        private void Record()
        {
            connection.BatchSizes.Add(_commands.Count);
            foreach (var command in _commands.Items)
            {
                connection.BatchedCommands.Add(command.CommandText);
                connection.Executions.Add((command.CommandText, DbTransaction, command.Parameters.Cast<DbParameter>().ToList()));
            }
        }
    }

    private sealed class BatchCommand : DbBatchCommand
    {
        private readonly Parameters _parameters = new();

        [AllowNull]
        public override string CommandText { get; set; } = "";

        public override CommandType CommandType { get; set; }

        public override int RecordsAffected => -1;

        public override bool CanCreateParameter => true;

        protected override DbParameterCollection DbParameterCollection => _parameters;

        public override DbParameter CreateParameter() => new Parameter();
    }

    private sealed class BatchCommands : DbBatchCommandCollection
    {
        public List<DbBatchCommand> Items { get; } = [];

        public override int Count => Items.Count;

        public override bool IsReadOnly => false;

        public override void Add(DbBatchCommand item) => Items.Add(item);

        public override void Clear() => Items.Clear();

        public override bool Contains(DbBatchCommand item) => Items.Contains(item);

        public override void CopyTo(DbBatchCommand[] array, int arrayIndex) => Items.CopyTo(array, arrayIndex);

        public override IEnumerator<DbBatchCommand> GetEnumerator() => Items.GetEnumerator();

        public override int IndexOf(DbBatchCommand item) => Items.IndexOf(item);

        public override void Insert(int index, DbBatchCommand item) => Items.Insert(index, item);

        public override bool Remove(DbBatchCommand item) => Items.Remove(item);

        public override void RemoveAt(int index) => Items.RemoveAt(index);

        protected override DbBatchCommand GetBatchCommand(int index) => Items[index];

        protected override void SetBatchCommand(int index, DbBatchCommand batchCommand) => Items[index] = batchCommand;
    }

    private sealed class Transaction(RecordingDbConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.Unspecified;

        protected override DbConnection DbConnection => connection;

        public override void Commit()
        {
            if (connection.FailCommitWith != null)
            {
                throw connection.FailCommitWith;
            }

            connection.Commits++;
        }

        public override void Rollback()
        {
            connection.Rollbacks++;
        }
    }

    private sealed class Command(RecordingDbConnection connection) : DbCommand
    {
        [AllowNull]
        public override string CommandText { get; set; } = "";

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; } = connection;

        protected override DbParameterCollection DbParameterCollection { get; } = new Parameters();

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
            connection.CancelCalls++;
        }

        public override void Prepare()
        {
        }

        public override int ExecuteNonQuery()
        {
            connection.Record(this);

            return 0;
        }

        public override object? ExecuteScalar()
        {
            connection.Record(this);

            return connection.Scalar?.Invoke(CommandText) ?? (long)connection.RowCount;
        }

        protected override DbParameter CreateDbParameter() => new Parameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            connection.Record(this);

            return new Reader(connection, connection.ColumnsOf(CommandText));
        }
    }

    private sealed class Parameters : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];

        public override int Count => _items.Count;

        public override object SyncRoot => this;

        public override int Add(object value)
        {
            _items.Add((DbParameter)value);

            return _items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var value in values)
            {
                Add(value);
            }
        }

        public override void Clear() => _items.Clear();

        public override bool Contains(object value) => _items.Contains(value);

        public override bool Contains(string value) => _items.Any(x => x.ParameterName == value);

        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

        public override IEnumerator GetEnumerator() => _items.GetEnumerator();

        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);

        public override int IndexOf(string parameterName) => _items.FindIndex(x => x.ParameterName == parameterName);

        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);

        public override void Remove(object value) => _items.Remove((DbParameter)value);

        public override void RemoveAt(int index) => _items.RemoveAt(index);

        public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));

        protected override DbParameter GetParameter(int index) => _items[index];

        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];

        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }

    private sealed class Parameter : DbParameter
    {
        public override DbType DbType { get; set; }

        public override ParameterDirection Direction { get; set; }

        public override bool IsNullable { get; set; }

        [AllowNull]
        public override string ParameterName { get; set; } = "";

        public override int Size { get; set; }

        [AllowNull]
        public override string SourceColumn { get; set; } = "";

        public override bool SourceColumnNullMapping { get; set; }

        public override object? Value { get; set; }

        public override void ResetDbType()
        {
        }
    }

    private IReadOnlyList<(string Name, Func<int, object?> Value)> ColumnsOf(string? commandText) =>
        (commandText == null ? null : ColumnsFor?.Invoke(commandText)) ?? Columns;

    private sealed class Reader(RecordingDbConnection connection, IReadOnlyList<(string Name, Func<int, object?> Value)> columns) : DbDataReader
    {
        private int _row = -1;
        private bool _closed;

        public override int Depth => 0;

        public override int FieldCount => columns.Count;

        public override bool HasRows => connection.RowCount > 0;

        public override bool IsClosed => _closed;

        public override int RecordsAffected => -1;

        public override object this[int ordinal] => GetValue(ordinal);

        public override object this[string name] => GetValue(GetOrdinal(name));

        public override bool Read()
        {
            if (_row + 1 >= connection.RowCount)
            {
                return false;
            }

            if (connection.FailReadingAtRow is { } failingRow && _row + 1 >= failingRow)
            {
                throw new InvalidOperationException("The connection to the server was lost.");
            }

            _row++;
            connection.RowsRead++;

            return true;
        }

        public override bool NextResult() => false;

        public override void Close()
        {
            var firstClose = !_closed;
            _closed = true;
            if (firstClose && connection.FailClosingReaderWith is { } failure)
            {
                throw failure;
            }
        }

        public override IEnumerator GetEnumerator() => throw new NotSupportedException();

        public override string GetName(int ordinal) => columns[ordinal].Name;

        public override int GetOrdinal(string name) =>
            columns
                .Select((x, i) => (x.Name, Index: i))
                .Where(x => x.Name == name)
                .Select(x => x.Index)
                .First();

        public override Type GetFieldType(int ordinal) => columns[ordinal].Value(0)?.GetType() ?? typeof(object);

        public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

        public override object GetValue(int ordinal) => columns[ordinal].Value(_row) ?? DBNull.Value;

        public override int GetValues(object[] values)
        {
            for (var ordinal = 0; ordinal < FieldCount; ordinal++)
            {
                values[ordinal] = GetValue(ordinal);
            }

            return FieldCount;
        }

        public override bool IsDBNull(int ordinal) => columns[ordinal].Value(_row) == null;

        public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

        public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

        public override char GetChar(int ordinal) => (char)GetValue(ordinal);

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

        public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal));

        public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal));

        public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));

        public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

        public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));

        public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));

        public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));

        public override string GetString(int ordinal) => (string)GetValue(ordinal);
    }
}
