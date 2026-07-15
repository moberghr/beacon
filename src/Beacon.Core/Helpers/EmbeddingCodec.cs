using System.Buffers.Binary;
using System.Globalization;

namespace Beacon.Core.Helpers;

/// <summary>
/// Provider-neutral round-trip between an embedding <c>float[]</c> and its stored <c>byte[]</c> form
/// (little-endian), plus a cosine-similarity helper. Lives in Core so both the PostgreSQL and SQL
/// Server data paths can decode <c>EmbeddingBytes</c> without depending on any vector-specific package.
/// </summary>
public static class EmbeddingCodec
{
    /// <summary>Serializes a vector to little-endian bytes (4 bytes per component).</summary>
    public static byte[] ToBytes(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    /// <summary>
    /// Formats a vector as a pgvector text literal (e.g. <c>[0.1,0.2,…]</c>) using invariant, round-trippable
    /// formatting. Matches the query-side literal in the PostgreSQL nearest-neighbor search so the stored
    /// vector and the query vector share one representation. Pure string helper — no EF / provider dependency.
    /// </summary>
    public static string ToVectorLiteral(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        return "[" + string.Join(",", vector.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";
    }

    /// <summary>Deserializes a little-endian byte array back into a vector.</summary>
    public static float[] FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length % sizeof(float) != 0)
        {
            throw new ArgumentException("Embedding byte length must be a multiple of 4.", nameof(bytes));
        }

        var vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        }

        return vector;
    }

    /// <summary>
    /// Cosine similarity in [-1, 1]. Returns 0 when either vector has zero norm (guards divide-by-zero).
    /// </summary>
    public static double Cosine(float[] a, float[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length != b.Length)
        {
            throw new ArgumentException("Vectors must have the same length.", nameof(b));
        }

        double dot = 0;
        double normA = 0;
        double normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        if (normA <= 0 || normB <= 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
