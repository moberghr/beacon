namespace Beacon.SampleProject.Authentication;

internal static class MiddlewarePathHelper
{
    private static readonly string[] StaticPrefixes = ["/_content/", "/_framework/", "/_blazor"];

    private static readonly string[] StaticExtensions =
        [".css", ".js", ".svg", ".png", ".jpg", ".jpeg", ".ico", ".woff", ".woff2", ".ttf"];

    public static bool IsStaticOrFrameworkPath(string path)
    {
        foreach (var prefix in StaticPrefixes)
        {
            if (path.Contains(prefix, StringComparison.OrdinalIgnoreCase))
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
