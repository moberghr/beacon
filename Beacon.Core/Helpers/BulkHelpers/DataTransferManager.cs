using System.Collections;
using System.Data;
using System.Diagnostics;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Beacon.Core.Helpers.BulkHelpers;

public class SqlServerDataTransferManager : IDisposable
{
    private readonly SqlConnection _destinationConnection;

    public SqlServerDataTransferManager(string connectionString)
    {
        _destinationConnection = new SqlConnection(connectionString);
        _destinationConnection.Open();
    }

    public void MergeData<T>(IEnumerable<T> data, Table table, Schema schema, bool overwriteDestination, bool updateOnlyChangedRows)
    {
        if (data == null)
        {
            return;
        }

        var reader = new ObjectDataReader<T>(data);
        var fullDestTableName = GetFullName(schema, table);

        // Copy data
        var columns = GetColumnNames(reader);
        var tempTableName = CreateTempTable(table.ToString(), columns, schema.ToString());
        var copy = new SqlBulkCopy(_destinationConnection, SqlBulkCopyOptions.Default, null)
        {
            DestinationTableName = tempTableName
        };

        copy.WriteToServer(reader);
        MergeData(tempTableName, columns, fullDestTableName, overwriteDestination, updateOnlyChangedRows);
        DropTempTable(tempTableName);
    }

    public void MergeData<T>(IEnumerable<T> data, string tableName, string? schemaName, bool overwriteDestination, bool updateOnlyChangedRows)
    {
        if (data == null)
        {
            return;
        }

        var reader = new ObjectDataReader<T>(data);
        var fullDestTableName = schemaName != null ? GetFullName(schemaName, tableName) : $"[dbo].{tableName}";

        // Copy data
        var columns = GetColumnNames(reader);
        var tempTableName = CreateTempTable(tableName, columns, schemaName ?? "dbo");
        var copy = new SqlBulkCopy(_destinationConnection, SqlBulkCopyOptions.Default, null)
        {
            DestinationTableName = tempTableName
        };

        copy.WriteToServer(reader);
        MergeData(tempTableName, columns, fullDestTableName, overwriteDestination, updateOnlyChangedRows);
        DropTempTable(tempTableName);
    }

    public void BulkInsert<T>(IEnumerable<T> data, Table table, Schema schema)
    {
        if (data == null)
        {
            return;
        }

        var reader = new ObjectDataReader<T>(data);

        var bulkCopy = new SqlBulkCopy(_destinationConnection, SqlBulkCopyOptions.TableLock | SqlBulkCopyOptions.FireTriggers | SqlBulkCopyOptions.UseInternalTransaction, null)
        {
            DestinationTableName = GetFullName(schema, table)
        };

        bulkCopy.WriteToServer(reader);
    }

    public void BulkInsert<T>(IEnumerable<T> data, string tableName, string? schemaName = null)
    {
        if (data == null)
        {
            return;
        }

        var reader = new ObjectDataReader<T>(data);
        var fullTableName = schemaName != null ? GetFullName(schemaName, tableName) : $"[dbo].{tableName}";

        var bulkCopy = new SqlBulkCopy(_destinationConnection, SqlBulkCopyOptions.TableLock | SqlBulkCopyOptions.FireTriggers | SqlBulkCopyOptions.UseInternalTransaction, null)
        {
            DestinationTableName = fullTableName
        };

        bulkCopy.WriteToServer(reader);
    }

    /// <summary>
    /// Writes data from source table to destination table in a way that inserts non existing rows, and updates existing rows
    /// </summary>
    private void MergeData(string sourceTableName, IEnumerable<string> sourceColumns, string destTableName, bool overwriteDestination, bool updateOnlyChanged)
    {
        var schemaTable = GetSchemaTable(destTableName);
        var keyList = new ArrayList();
        foreach (DataRow row in schemaTable.Rows)
        {
            if ((bool)row["IsKey"])
            {
                keyList.Add(row["ColumnName"].ToString());
            }
        }

        var updateColumns = new StringBuilder();
        var insertParams = new StringBuilder();
        var insertValues = new StringBuilder();

        var where = new StringBuilder();
        var whereChanged = new StringBuilder();

        // Handle inserting primary keys to support insert/update
        foreach (string keyName in keyList)
        {
            if (where.Length != 0)
            {
                where.Append(" AND ");
            }

            where.Append(string.Format("{0}.{2}={1}.{2}", "D", "S", keyName));

            if (whereChanged.Length != 0)
            {
                whereChanged.Append(" AND ");
            }

            whereChanged.Append(string.Format("{0}.{2}={1}.{2}", "D", "S", keyName));
        }

        // Surround with parenthesis to force the order of execution
        whereChanged.Append(" AND (");
        var i = 0;
        foreach (var columnName in sourceColumns)
        {
            if (!keyList.Contains(columnName))
            {
                if (updateColumns.Length != 0)
                {
                    updateColumns.Append(", ");
                }

                updateColumns.Append(string.Format("{0}.{2}={1}.{2}", "D", "S", columnName));

                if (i == 0)
                {
                    whereChanged.Append(string.Format("{0}.{2}!={1}.{2}", "D", "S", columnName));
                }
                else
                {
                    whereChanged.Append(string.Format(" OR {0}.{2}!={1}.{2}", "D", "S", columnName));
                }

                i++;
            }

            if (insertParams.Length != 0)
            {
                insertParams.Append(", ");
            }

            insertParams.Append(columnName);

            if (insertValues.Length != 0)
            {
                insertValues.Append(", ");
            }

            insertValues.Append("S." + columnName);
        }

        whereChanged.Append(")");

        var updateWhere = updateOnlyChanged ? whereChanged.ToString() : where.ToString();
        var deleteSql = $"DELETE {destTableName} FROM {destTableName} D WHERE NOT EXISTS (SELECT * FROM {sourceTableName} S WHERE {where})";
        var updateSql = $"UPDATE D SET {updateColumns} FROM {destTableName} D INNER JOIN {sourceTableName} S ON {updateWhere}";
        var insertSql = $"INSERT INTO {destTableName} ({insertParams}) SELECT {insertParams} FROM {sourceTableName} S WHERE NOT EXISTS (SELECT * FROM {destTableName} D WHERE {where})";

        var cmd = CreateCommand(string.Empty);
        if (overwriteDestination)
        {
            cmd.CommandText = deleteSql;
            cmd.ExecuteNonQuery();
        }

        if (updateColumns.ToString() != string.Empty)
        {
            cmd.CommandText = updateSql;
            cmd.ExecuteNonQuery();
        }

        cmd.CommandText = insertSql;
        cmd.ExecuteNonQuery();
    }

    private SqlCommand CreateCommand(string commandText)
    {
        var result = _destinationConnection.CreateCommand();
        result.CommandText = commandText;

        return result;
    }

    private string[] GetColumnNames(IDataReader reader)
    {
        var result = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            result[i] = reader.GetName(i);
        }

        return result;
    }

    private string CreateTempTable(string tableName, string[] columns, string schemaName)
    {
        var tempTableName = $"#{tableName}";
        var select = new StringBuilder();
        foreach (var t in columns)
        {
            if (@select.Length != 0)
            {
                @select.Append(", ");
            }

            @select.Append(t);
        }

        var cmd = CreateCommand($"SELECT TOP 0 {select} INTO {tempTableName} FROM {GetFullName(schemaName, tableName)}");
        cmd.ExecuteNonQuery();

        return tempTableName;
    }

    private void DropTempTable(string tempTableName)
    {
        Debug.Assert(tempTableName.StartsWith("#"));
        var cmd = CreateCommand($"DROP TABLE {tempTableName}");
        cmd.ExecuteNonQuery();
    }

    private DataTable GetSchemaTable(string tableName)
    {
        var schemaCommand = CreateCommand($"SELECT TOP 0 * FROM {tableName}");
        var schemaReader = schemaCommand.ExecuteReader(CommandBehavior.KeyInfo);
        var schemaTable = schemaReader.GetSchemaTable();
        schemaReader.Close();

        return schemaTable;
    }

    private string GetFullName(string schemaName, string tableName) => $"[{schemaName}].{tableName}";

    private string GetFullName(Schema schema, Table table) => GetFullName(schema.ToString(), table.ToString());

    public void Dispose()
    {
        _destinationConnection.Close();
        _destinationConnection.Dispose();
    }
}

public enum Table
{
    ClaimPayment = 0,
    Claim = 1,
    AccountTransaction = 2,
    CommunicationLogEntity = 3,
    CreditHistory = 4,
    VerificationRequest = 5
}

public enum Schema
{
    dbo = 0,
    banking = 1,
    logging = 2
}