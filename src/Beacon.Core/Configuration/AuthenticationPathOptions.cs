using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>
/// Where Beacon's authentication middlewares apply, bound from <c>Beacon:Authentication</c>: Beacon's own routes under
/// <see cref="BeaconPath"/>, plus the path prefixes a host lists in <see cref="AdditionalPaths"/>. Every other route belongs
/// to the host: a Beacon API key, bearer token or <c>Beacon.Auth</c> cookie does not authenticate it, and the login and
/// first-run redirects leave it alone.
/// </summary>
public sealed class AuthenticationPathOptions
{
    public const string SectionName = "Beacon:Authentication";

    /// <summary>Beacon's own routes: the UI, the REST API, the realtime hub and the MCP server.</summary>
    public const string BeaconPath = "/beacon";

    /// <summary>
    /// More path prefixes where Beacon credentials apply, for example <c>/warp</c> for a Warp dashboard that admits Beacon
    /// admins. Matched by whole segments, ignoring case, against the request path after the host's path base. Each entry
    /// starts with '/', is not '/' alone and carries no query or fragment; a trailing '/' is ignored.
    /// </summary>
    public List<string> AdditionalPaths { get; set; } = [];

    /// <summary>The entry as it is matched: trimmed, without a trailing '/'.</summary>
    public static string Normalize(string? path)
    {
        return (path ?? string.Empty).Trim().TrimEnd('/');
    }

    /// <summary>Whether a normalized entry is a path prefix that can be matched (see <see cref="AdditionalPaths"/>).</summary>
    public static bool IsValidPrefix(string normalized)
    {
        return normalized.Length > 1
            && normalized[0] == '/'
            && normalized.IndexOfAny(['?', '#']) < 0;
    }
}

internal sealed class AuthenticationPathOptionsValidator : IValidateOptions<AuthenticationPathOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthenticationPathOptions options)
    {
        var failures = new List<string>();
        for (var index = 0; index < options.AdditionalPaths.Count; index++)
        {
            if (!AuthenticationPathOptions.IsValidPrefix(AuthenticationPathOptions.Normalize(options.AdditionalPaths[index])))
            {
                failures.Add(
                    $"{AuthenticationPathOptions.SectionName}:{nameof(AuthenticationPathOptions.AdditionalPaths)}:{index} must be a path prefix such as /warp: it starts with '/', is not '/' alone and has no query or fragment.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
