using System.Collections;
using System.Numerics;
using Beacon.Core.Configuration;
using Beacon.Core.Models;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// The bytes a result read so far is estimated to hold, against <c>Beacon:Query:MaxResultBytes</c>. Each row costs
/// <see cref="RowOverheadBytes"/> plus <see cref="ColumnOverheadBytes"/> a column, and each value at least
/// <see cref="MinimumValueBytes"/>: a text value two bytes a character, a binary value its length, an array of a primitive
/// type its memory size, a <see cref="BigInteger"/> its byte count, a number, date, time, interval or identifier
/// <see cref="FixedValueBytes"/>, and any other value two bytes a character of its text (<see cref="object.ToString"/>).
/// An array, collection or dictionary costs its elements (keys and values), counted the same way however deeply they
/// nest (an empty one <see cref="MinimumValueBytes"/>); the walk stops as soon as the value is known to pass what is left
/// of the limit, or after it has visited one element for every <see cref="MinimumValueBytes"/> left, so a structure that
/// holds itself still ends, as over the limit. A read that passes the limit stops and fails
/// (<see cref="ResultTooLargeException"/>); no value or row is ever cut short.
/// </summary>
internal sealed class ResultByteBudget(long limit)
{
    internal const int RowOverheadBytes = 32;

    internal const int ColumnOverheadBytes = 8;

    internal const int MinimumValueBytes = 8;

    internal const int FixedValueBytes = 16;

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
            if (AddValue(value))
            {
                break;
            }
        }

        return Exceeded;
    }

    /// <summary>Counts the overhead of a row of <paramref name="columnCount"/> values, before its values.</summary>
    public bool AddRow(int columnCount)
    {
        Count(RowOverheadBytes + (long)ColumnOverheadBytes * columnCount);

        return Exceeded;
    }

    /// <summary>Counts one value; true when the result read so far is over the limit.</summary>
    public bool AddValue(object? value)
    {
        Count(EstimatedBytes(value, Math.Max(0, limit - _used)));

        return Exceeded;
    }

    /// <summary>The estimate of <paramref name="value"/>, walked without a bound: for values known to be finite.</summary>
    internal static long EstimatedBytes(object? value) => EstimatedBytes(value, long.MaxValue);

    /// <summary>
    /// The estimate of <paramref name="value"/>, or, once it is known to pass <paramref name="cap"/>, a number larger than
    /// <paramref name="cap"/> (<see cref="long.MaxValue"/> when the walk stopped at its element bound).
    /// </summary>
    internal static long EstimatedBytes(object? value, long cap) =>
        IsWalked(value) ? WalkedBytes(value!, cap) : ScalarBytes(value);

    internal static string Message(long limit) =>
        $"The result is larger than the {limit}-byte limit ({QueryExecutionOptions.SectionName}:{nameof(QueryExecutionOptions.MaxResultBytes)}); select fewer columns or rows.";

    private void Count(long bytes)
    {
        _used = bytes > long.MaxValue - _used ? long.MaxValue : _used + bytes;
    }

    private static long WalkedBytes(object root, long cap)
    {
        var maxElements = Math.Max(1, cap / MinimumValueBytes);
        var elements = 0L;
        var bytes = 0L;
        var pending = new Stack<object>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var empty = true;
            foreach (var element in ElementsOf(pending.Pop()))
            {
                empty = false;
                elements++;
                if (elements > maxElements)
                {
                    return long.MaxValue;
                }

                if (IsWalked(element))
                {
                    pending.Push(element!);
                    continue;
                }

                bytes += ScalarBytes(element);
                if (bytes > cap)
                {
                    return bytes;
                }
            }

            if (empty)
            {
                bytes += MinimumValueBytes;
                if (bytes > cap)
                {
                    return bytes;
                }
            }
        }

        return bytes;
    }

    private static IEnumerable<object?> ElementsOf(object container)
    {
        if (container is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                yield return entry.Key;
                yield return entry.Value;
            }

            yield break;
        }

        foreach (var element in (IEnumerable)container)
        {
            yield return element;
        }
    }

    private static bool IsWalked(object? value) =>
        value is IEnumerable and not string && !IsPrimitiveArray(value);

    private static bool IsPrimitiveArray(object value) =>
        value is Array array && array.GetType().GetElementType() is { IsPrimitive: true };

    private static long ScalarBytes(object? value)
    {
        var bytes = value switch
        {
            null or DBNull => 0,
            string text => (long)text.Length * 2,
            char[] characters => (long)characters.Length * 2,
            byte[] binary => binary.Length,
            Array array when IsPrimitiveArray(array) => Buffer.ByteLength(array),
            BigInteger number => number.GetByteCount(),
            ValueType fixedSize when IsFixedSize(fixedSize.GetType()) => FixedValueBytes,
            { } other => (long)(other.ToString()?.Length ?? 0) * 2
        };

        return Math.Max(MinimumValueBytes, bytes);
    }

    private static bool IsFixedSize(Type type) =>
        type.IsPrimitive
        || type.IsEnum
        || type == typeof(decimal)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(DateOnly)
        || type == typeof(TimeOnly)
        || type == typeof(TimeSpan)
        || type == typeof(Guid);
}

/// <summary>A read stopped because its result passed <c>Beacon:Query:MaxResultBytes</c>. The message holds no value.</summary>
internal sealed class ResultTooLargeException(long limit) : BeaconException(ResultByteBudget.Message(limit));
