using Microsoft.AspNetCore.Http;

namespace Beacon.Api.Authentication;

internal static class MiddlewarePathHelper
{
    private static readonly string[] StaticPrefixes = ["/_content/"];

    private static readonly string[] StaticExtensions =
        [".css", ".js", ".svg", ".png", ".jpg", ".jpeg", ".ico", ".woff", ".woff2", ".ttf"];

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="segment"/> (for example <c>/setup</c>) or sits below it, at the
    /// root or under Beacon's base path. Whole path segments only: <c>/setup-guide</c> and <c>/x/setup</c> do not match.
    /// </summary>
    public static bool IsUnderSegment(PathString path, string basePath, string segment)
    {
        return path.StartsWithSegments(segment, StringComparison.OrdinalIgnoreCase)
            || (basePath.Length > 0 && path.StartsWithSegments($"{basePath}{segment}", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsStaticOrFrameworkPath(string path, string basePath = "")
    {
        foreach (var prefix in StaticPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || (basePath.Length > 0 && path.StartsWith($"{basePath}{prefix}", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        foreach (var ext in StaticExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
