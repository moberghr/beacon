using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Beacon.Tests.Common;

/// <summary>
/// A <see cref="DbBatch"/> for ADO.NET connection doubles: it runs each of its commands as an ordinary command of the
/// connection (same transaction, same parameters), so a double that records commands records a batch's commands too, and
/// returns the last command's reader. <see cref="Sizes"/> holds the number of commands of every batch run. No database
/// (§4.7).
/// </summary>
internal sealed class CommandBackedDbBatch(DbConnection connection, List<int>? sizes = null) : DbBatch
{
    private readonly Commands _commands = new();

    public List<int> Sizes { get; } = sizes ?? [];

    public override int Timeout { get; set; }

    protected override DbBatchCommandCollection DbBatchCommands => _commands;

    protected override DbConnection? DbConnection { get; set; } = connection;

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
    }

    public override int ExecuteNonQuery()
    {
        Sizes.Add(_commands.Count);
        foreach (var command in _commands.Items)
        {
            ToCommand(command).ExecuteNonQuery();
        }

        return 0;
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default) => Task.FromResult(ExecuteNonQuery());

    public override object? ExecuteScalar()
    {
        Sizes.Add(_commands.Count);
        object? result = null;
        foreach (var command in _commands.Items)
        {
            result = ToCommand(command).ExecuteScalar();
        }

        return result;
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default) => Task.FromResult(ExecuteScalar());

    public override void Prepare()
    {
    }

    public override Task PrepareAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    protected override DbBatchCommand CreateDbBatchCommand() => new Command(connection);

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        Sizes.Add(_commands.Count);
        DbDataReader? reader = null;
        foreach (var command in _commands.Items)
        {
            reader?.Dispose();
            reader = ToCommand(command).ExecuteReader(behavior);
        }

        return reader ?? throw new InvalidOperationException("The batch holds no command.");
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
        Task.FromResult(ExecuteDbDataReader(behavior));

    private DbCommand ToCommand(DbBatchCommand batchCommand)
    {
        var command = connection.CreateCommand();
        command.CommandText = batchCommand.CommandText;
        command.CommandTimeout = Timeout;
        command.Transaction = DbTransaction;
        foreach (DbParameter parameter in batchCommand.Parameters)
        {
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private sealed class Command(DbConnection connection) : DbBatchCommand
    {
        private readonly DbParameterCollection _parameters = connection.CreateCommand().Parameters;

        [AllowNull]
        public override string CommandText { get; set; } = "";

        public override CommandType CommandType { get; set; }

        public override int RecordsAffected => -1;

        public override bool CanCreateParameter => true;

        protected override DbParameterCollection DbParameterCollection => _parameters;

        public override DbParameter CreateParameter() => connection.CreateCommand().CreateParameter();
    }

    private sealed class Commands : DbBatchCommandCollection
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
}
