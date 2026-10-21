using System.Text.Json;

namespace Beacon.Doctor;

/// <summary><c>beacon-doctor mcp &lt;url&gt; [--token &lt;access token&gt;] [--json]</c>.</summary>
internal static class DoctorProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 2 || args[0] != "mcp" || !Uri.TryCreate(args[1], UriKind.Absolute, out var endpoint))
        {
            Console.Error.WriteLine("Usage: beacon-doctor mcp <https://host/beacon/mcp> [--token <access token>] [--json]");
            Console.Error.WriteLine("The token is sent to that endpoint only. Without one, the sign-in path is checked but not the handshake.");
            return 2;
        }

        var token = Option(args, "--token") ?? Environment.GetEnvironmentVariable("BEACON_MCP_TOKEN");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var results = await new McpDoctor(http).RunAsync(endpoint, token, CancellationToken.None);

        if (args.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(results.Select(x => new { status = x.Status.ToString().ToLowerInvariant(), check = x.Check, detail = x.Detail })));
        }
        else
        {
            foreach (var result in results)
            {
                Console.WriteLine($"{result.Status.ToString().ToUpperInvariant(),-4}  {result.Check,-22} {result.Detail}");
            }
        }

        return results.Any(x => x.Status == CheckStatus.Fail) ? 1 : 0;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
