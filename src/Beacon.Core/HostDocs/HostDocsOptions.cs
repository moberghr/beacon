using System.Reflection;

namespace Beacon.Core.HostDocs;

/// <summary>
/// Configures documents a host ships with its deployment (markdown / text files on disk or embedded resources) to be
/// imported into a Beacon project at startup. Only <c>.md</c>, <c>.markdown</c> and <c>.txt</c> files are ingested.
/// </summary>
public sealed class HostDocsOptions
{
    /// <summary>Default per-document size cap (512 KB). Larger documents are skipped with a warning.</summary>
    public const int DefaultMaxDocumentBytes = 512 * 1024;

    /// <summary>The glob a directory source uses when none is given.</summary>
    public const string DefaultGlob = "**/*.md";

    private readonly List<HostDocsSource> _sources = [];
    private readonly List<string> _excludeGlobs = [];

    /// <summary>
    /// REQUIRED. The project the documents attach to, matched by name — normally the same project
    /// <c>ExposeDbContext</c> uses. Created when missing.
    /// </summary>
    public string? ProjectName { get; set; }

    /// <summary>Per-document size cap in bytes. Defaults to <see cref="DefaultMaxDocumentBytes"/>.</summary>
    public int MaxDocumentBytes { get; set; } = DefaultMaxDocumentBytes;

    internal IReadOnlyList<HostDocsSource> Sources => _sources;

    internal IReadOnlyList<string> ExcludeGlobs => _excludeGlobs;

    /// <summary>
    /// Imports files under <paramref name="path"/> (relative to the host's <c>ContentRootPath</c>, or absolute)
    /// matching <paramref name="globs"/> (default <see cref="DefaultGlob"/>). Files resolving outside the directory,
    /// and symbolic links inside it, are rejected.
    /// </summary>
    public HostDocsOptions FromDirectory(string path, params string[] globs)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("ExposeDocs: FromDirectory requires a path.", nameof(path));
        }

        var patterns = globs.Length == 0 ? [DefaultGlob] : globs;
        foreach (var glob in patterns)
        {
            ValidateGlob(glob);
        }

        _sources.Add(new HostDocsDirectorySource(path.Trim(), patterns));
        return this;
    }

    /// <summary>
    /// Imports the manifest resources of <paramref name="assembly"/> whose name starts with
    /// <paramref name="resourcePrefix"/>; the remainder of the name is the document path. Give the resources a
    /// <c>LogicalName</c> such as <c>docs/wiki/%(RecursiveDir)%(Filename)%(Extension)</c> to keep folder names intact.
    /// </summary>
    public HostDocsOptions FromEmbeddedResources(Assembly assembly, string resourcePrefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        _sources.Add(new HostDocsResourceSource(assembly, resourcePrefix ?? string.Empty));
        return this;
    }

    /// <summary>Excludes documents whose relative path matches any of <paramref name="globs"/> (every source of this call).</summary>
    public HostDocsOptions Exclude(params string[] globs)
    {
        foreach (var glob in globs)
        {
            ValidateGlob(glob);
            _excludeGlobs.Add(glob);
        }

        return this;
    }

    private static void ValidateGlob(string glob)
    {
        if (string.IsNullOrWhiteSpace(glob))
        {
            throw new ArgumentException("ExposeDocs: a glob must not be empty.");
        }

        var segments = glob.Replace('\\', '/').Split('/');
        if (Path.IsPathRooted(glob) || segments.Contains(".."))
        {
            throw new ArgumentException($"ExposeDocs: glob '{glob}' must be relative and must not contain '..'.");
        }
    }
}

/// <summary>One place documents are read from.</summary>
internal abstract record HostDocsSource
{
    /// <summary>Stable identity persisted as <c>ProjectImportedDocument.SourceKey</c>.</summary>
    public abstract string Key { get; }
}

internal sealed record HostDocsDirectorySource(string Path, IReadOnlyList<string> Globs) : HostDocsSource
{
    public override string Key => "docs:dir:" + Path.Replace('\\', '/').TrimEnd('/');
}

internal sealed record HostDocsResourceSource(Assembly Assembly, string Prefix) : HostDocsSource
{
    public override string Key => $"docs:res:{Assembly.GetName().Name}:{Prefix}";
}
