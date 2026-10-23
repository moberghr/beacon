using Beacon.Core.Configuration;
using Microsoft.AspNetCore.Http;

namespace Beacon.Api.Authentication;

internal static class MiddlewarePathHelper
{
    private static readonly string[] StaticPrefixes = ["/_content/"];

    private static readonly string[] StaticExtensions =
        [".css", ".js", ".svg", ".png", ".jpg", ".jpeg", ".ico", ".woff", ".woff2", ".ttf"];

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="segment"/> (for example <c>/setup</c>) or sits below it, under
    /// Beacon's base path or one of the configured additional prefixes. Whole path segments only: <c>/beacon/setup-guide</c>
    /// and <c>/beacon/x/setup</c> do not match, and neither does the bare root (<c>/setup</c>), which is no Beacon route.
    /// </summary>
    public static bool IsUnderSegment(PathString path, string basePath, string segment, AuthenticationPathPrefixes? prefixes = null)
    {
        foreach (var root in Roots(basePath, prefixes))
        {
            if (path.StartsWithSegments($"{root}{segment}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsStaticOrFrameworkPath(string path, string basePath = "", AuthenticationPathPrefixes? prefixes = null)
    {
        foreach (var prefix in StaticPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (var root in Roots(basePath, prefixes))
            {
                if (path.StartsWith($"{root}{prefix}", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        foreach (var ext in StaticExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> Roots(string basePath, AuthenticationPathPrefixes? prefixes)
    {
        if (basePath.Length > 0)
        {
            yield return basePath;
        }

        if (prefixes != null)
        {
            foreach (var prefix in prefixes.Prefixes)
            {
                yield return prefix;
            }
        }
    }
}
