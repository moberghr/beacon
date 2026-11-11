using System.Text.Json;

namespace Beacon.Doctor;

/// <summary>
/// <c>beacon-doctor mcp &lt;url&gt; [--token-stdin] [--json]</c>, with the access token in <c>BEACON_MCP_TOKEN</c> or on
/// standard input. <c>--token &lt;access token&gt;</c> still works, with a warning: it leaves the token in the process list
/// and the shell history.
/// </summary>
internal static class DoctorProgram
{
    public const string TokenVariable = "BEACON_MCP_TOKEN";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 2 || args[0] != "mcp" || !Uri.TryCreate(args[1], UriKind.Absolute, out var endpoint))
        {
            Console.Error.WriteLine("Usage: beacon-doctor mcp <https://host/beacon/mcp> [--token-stdin] [--json]");
            Console.Error.WriteLine($"The access token comes from the {TokenVariable} environment variable, or from standard input with --token-stdin.");
            Console.Error.WriteLine("The token is sent to that endpoint only. Without one, the sign-in path is checked but not the handshake.");
            return 2;
        }

        var token = ReadToken(args, Environment.GetEnvironmentVariable(TokenVariable), Console.In, Console.Error);
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

    /// <summary>
    /// The access token: the first line of <paramref name="input"/> with <c>--token-stdin</c>, else the value after
    /// <c>--token</c> (with a one-line warning on <paramref name="warnings"/>), else <paramref name="environmentToken"/>;
    /// null when none is given.
    /// </summary>
    internal static string? ReadToken(string[] args, string? environmentToken, TextReader input, TextWriter warnings)
    {
        var argument = Option(args, "--token");
        if (argument != null)
        {
            warnings.WriteLine($"warning: --token leaves the token in the process list and shell history; use {TokenVariable} or --token-stdin.");
        }

        if (args.Contains("--token-stdin"))
        {
            return NonEmpty(input.ReadLine());
        }

        return NonEmpty(argument) ?? NonEmpty(environmentToken);
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string? NonEmpty(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
