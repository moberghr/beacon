using System.Collections;
using Beacon.Core.Configuration;
using Beacon.Core.Models;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// The bytes a result read so far is estimated to hold, against <c>Beacon:Query:MaxResultBytes</c>. Each row costs
/// <see cref="RowOverheadBytes"/> plus <see cref="ColumnOverheadBytes"/> a column, and each value at least
/// <see cref="MinimumValueBytes"/>: a text value two bytes a character, a binary value its length, an array, collection or
/// dictionary its elements (keys and values), counted the same way down to <see cref="MaxDepth"/> levels (deeper, a fixed
/// <see cref="OtherValueBytes"/>), an array of a primitive type its memory size, and any other value
/// <see cref="OtherValueBytes"/>. A read that passes the limit stops and fails (<see cref="ResultTooLargeException"/>); no
/// value or row is ever cut short.
/// </summary>
internal sealed class ResultByteBudget(long limit)
{
    internal const int RowOverheadBytes = 32;

    internal const int ColumnOverheadBytes = 8;

    internal const int MinimumValueBytes = 8;

    internal const int OtherValueBytes = 16;

    internal const int MaxDepth = 4;

    private long _used;

    /// <summary>The bytes counted so far.</summary>
    public long Used => _used;

    public bool Exceeded => _used > limit;

    /// <summary>Counts one row's values with its overhead; true when the result read so far is over the limit.</summary>
    public bool Add(ICollection<object?> values)
    {
        AddRow(values.Count);
        foreach (var value in values)
        {
            AddValue(value);
        }

        return Exceeded;
    }

    /// <summary>Counts the overhead of a row of <paramref name="columnCount"/> values, before its values.</summary>
    public bool AddRow(int columnCount)
    {
        _used += RowOverheadBytes + (long)ColumnOverheadBytes * columnCount;

        return Exceeded;
    }

    /// <summary>Counts one value; true when the result read so far is over the limit.</summary>
    public bool AddValue(object? value)
    {
        _used += EstimatedBytes(value);

        return Exceeded;
    }

    internal static long EstimatedBytes(object? value) => EstimatedBytes(value, 0);

    internal static string Message(long limit) =>
        $"The result is larger than the {limit}-byte limit ({QueryExecutionOptions.SectionName}:{nameof(QueryExecutionOptions.MaxResultBytes)}); select fewer columns or rows.";

    private static long EstimatedBytes(object? value, int depth)
    {
        var bytes = value switch
        {
            null or DBNull => 0,
            string text => (long)text.Length * 2,
            char[] characters => (long)characters.Length * 2,
            byte[] binary => binary.Length,
            Array array when depth < MaxDepth && array.GetType().GetElementType() is { IsPrimitive: true } => Buffer.ByteLength(array),
            IDictionary dictionary when depth < MaxDepth => Entries(dictionary, depth + 1),
            IEnumerable sequence when depth < MaxDepth => Elements(sequence, depth + 1),
            _ => OtherValueBytes
        };

        return Math.Max(MinimumValueBytes, bytes);
    }

    private static long Entries(IDictionary dictionary, int depth)
    {
        var bytes = 0L;
        foreach (DictionaryEntry entry in dictionary)
        {
            bytes += EstimatedBytes(entry.Key, depth) + EstimatedBytes(entry.Value, depth);
        }

        return bytes;
    }

    private static long Elements(IEnumerable sequence, int depth)
    {
        var bytes = 0L;
        foreach (var element in sequence)
        {
            bytes += EstimatedBytes(element, depth);
        }

        return bytes;
    }
}

/// <summary>A read stopped because its result passed <c>Beacon:Query:MaxResultBytes</c>. The message holds no value.</summary>
internal sealed class ResultTooLargeException(long limit) : BeaconException(ResultByteBudget.Message(limit));
