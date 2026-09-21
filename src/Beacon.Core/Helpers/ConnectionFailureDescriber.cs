namespace Beacon.Core.Helpers;

/// <summary>
/// Builds the operator-facing description of a failed connection attempt.
/// ADO.NET providers put the actionable cause in the inner exception — Npgsql wraps a
/// <c>SocketException</c>, SQL Server wraps a <c>Win32Exception</c> — so surfacing only
/// <c>ex.Message</c> loses the reason ("No such host is known", "Connection refused").
/// The whole chain is flattened, newest first, each entry prefixed with its exception type.
/// </summary>
public static class ConnectionFailureDescriber
{
    private const int MaxDepth = 5;

    public static string Describe(Exception exception)
    {
        var parts = new List<string>();

        for (var current = exception; current != null && parts.Count < MaxDepth; current = current.InnerException)
        {
            var part = $"{current.GetType().Name}: {current.Message}";
            if (!parts.Contains(part))
            {
                parts.Add(part);
            }
        }

        return string.Join(" -> ", parts);
    }
}
