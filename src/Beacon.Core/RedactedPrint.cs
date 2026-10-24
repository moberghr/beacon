namespace Beacon.Core;

/// <summary>
/// How a record that carries a secret (a password, key, token, connection string or delivery destination) prints it in
/// its <c>PrintMembers</c>, so the compiler-generated <c>ToString()</c> a host's request logging may call never shows it:
/// <see cref="Mask"/> when the member holds a value, nothing when it is null or empty.
/// </summary>
internal static class RedactedPrint
{
    public const string Mask = "***";

    public static string Of(string? secret)
    {
        return string.IsNullOrEmpty(secret) ? string.Empty : Mask;
    }
}
