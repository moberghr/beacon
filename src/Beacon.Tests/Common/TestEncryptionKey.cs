namespace Beacon.Tests.Common;

/// <summary>
/// A <c>Beacon:EncryptionKey</c> that passes the startup checks (base64 of 32 bytes that are neither text nor a repeated
/// pattern), fixed so every run is the same. Not a key any deployment uses.
/// </summary>
internal static class TestEncryptionKey
{
    public static readonly string Value = Convert.ToBase64String(Enumerable.Range(0, 32)
        .Select(x => (byte)((x * 37) + 11))
        .ToArray());
}
