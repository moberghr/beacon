using System.Security.Cryptography;
using System.Text;

namespace Beacon.AI.Services.Embeddings;

internal static class EmbeddingContentHash
{
    public static string Compute(string text, string? modelFingerprint = null) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.IsNullOrEmpty(modelFingerprint) ? text : $"{modelFingerprint}\n{text}")));
}
