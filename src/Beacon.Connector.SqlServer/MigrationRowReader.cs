using System.Collections;
using System.Data.Common;
using System.Globalization;
using Beacon.Core.DataMigration;

namespace Beacon.Connector.SqlServer;

/// <summary>
/// A forward-only reader over <see cref="MigrationRows"/> for <c>SqlBulkCopy</c>: one field per column, in column order. A
/// null value, or a row without the column, reads as <see cref="DBNull"/>. Column names resolve exactly first, then
/// ignoring case (the way SQL Server compares names under its default collation).
/// </summary>
internal sealed class MigrationRowReader(MigrationRows rows) : DbDataReader
{
    private readonly Dictionary<string, int> _exactOrdinals = OrdinalsOf(rows.Columns, StringComparer.Ordinal);
    private readonly Dictionary<string, int> _ordinals = OrdinalsOf(rows.Columns, StringComparer.OrdinalIgnoreCase);
    private readonly Type?[] _fieldTypes = new Type?[rows.Columns.Count];
    private int _position = -1;
    private bool _closed;

    public override int FieldCount => rows.Columns.Count;

    public override int Depth => 0;

    public override bool HasRows => rows.Rows.Count > 0;

    public override bool IsClosed => _closed;

    public override int RecordsAffected => -1;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        if (_closed || _position >= rows.Rows.Count - 1)
        {
            _position = rows.Rows.Count;
            return false;
        }

        _position++;

        return true;
    }

    public override bool NextResult()
    {
        return false;
    }

    public override void Close()
    {
        _closed = true;
    }

    public override string GetName(int ordinal)
    {
        return rows.Columns[ordinal];
    }

    public override int GetOrdinal(string name)
    {
        if (_exactOrdinals.TryGetValue(name, out var exact))
        {
            return exact;
        }

        if (_ordinals.TryGetValue(name, out var ordinal))
        {
            return ordinal;
        }

        throw new IndexOutOfRangeException($"The migration rows have no column '{name}'.");
    }

    public override object GetValue(int ordinal)
    {
        if (_position < 0 || _position >= rows.Rows.Count)
        {
            throw new InvalidOperationException("The reader is not positioned on a row.");
        }

        var column = rows.Columns[ordinal];

        return rows.Rows[_position].TryGetValue(column, out var value) && value != null ? value : DBNull.Value;
    }

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public override bool IsDBNull(int ordinal)
    {
        return GetValue(ordinal) is DBNull;
    }

    // The CLR type of the column's first non-null value; object when every value is null.
    // The type of the column's first non-null value, found once per column and kept.
    public override Type GetFieldType(int ordinal)
    {
        return _fieldTypes[ordinal] ??= FieldTypeOf(rows.Columns[ordinal]);
    }

    public override string GetDataTypeName(int ordinal)
    {
        return GetFieldType(ordinal).Name;
    }

    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(GetValue(ordinal), CultureInfo.InvariantCulture);

    public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal), CultureInfo.InvariantCulture);

    public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal), CultureInfo.InvariantCulture);

    public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(GetValue(ordinal), CultureInfo.InvariantCulture);

    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal), CultureInfo.InvariantCulture);

    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal), CultureInfo.InvariantCulture);

    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal), CultureInfo.InvariantCulture);

    public override Guid GetGuid(int ordinal) => GetValue(ordinal) is Guid guid ? guid : Guid.Parse(GetString(ordinal));

    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal), CultureInfo.InvariantCulture);

    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal), CultureInfo.InvariantCulture);

    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal), CultureInfo.InvariantCulture);

    public override string GetString(int ordinal)
    {
        var value = GetValue(ordinal);

        return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    // Streamed binary columns read through here (DbDataReader.GetStream); only a byte array has bytes to give.
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        if (GetValue(ordinal) is not byte[] bytes)
        {
            throw new InvalidCastException($"Column '{GetName(ordinal)}' does not hold binary data.");
        }

        if (buffer == null)
        {
            return bytes.Length;
        }

        var count = (int)Math.Max(0, Math.Min(length, bytes.Length - dataOffset));
        Array.Copy(bytes, dataOffset, buffer, bufferOffset, count);

        return count;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var text = GetString(ordinal);
        if (buffer == null)
        {
            return text.Length;
        }

        var count = (int)Math.Max(0, Math.Min(length, text.Length - dataOffset));
        text.CopyTo((int)dataOffset, buffer, bufferOffset, count);

        return count;
    }

    public override IEnumerator GetEnumerator()
    {
        return new DbEnumerator(this, closeReader: false);
    }

    private static Dictionary<string, int> OrdinalsOf(IReadOnlyList<string> columns, StringComparer comparer)
    {
        var ordinals = new Dictionary<string, int>(comparer);
        for (var i = 0; i < columns.Count; i++)
        {
            ordinals.TryAdd(columns[i], i);
        }

        return ordinals;
    }

    private Type FieldTypeOf(string column)
    {
        foreach (var row in rows.Rows)
        {
            if (row.TryGetValue(column, out var value) && value != null)
            {
                return value.GetType();
            }
        }

        return typeof(object);
    }
}
