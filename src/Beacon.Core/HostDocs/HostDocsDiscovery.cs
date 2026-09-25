using System.Security.Cryptography;
using System.Text;
using Beacon.Core.Data.Entities.Projects;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Beacon.Core.HostDocs;

/// <summary>A document found in a host source, before parsing. <see cref="ContentHash"/> is over the raw bytes.</summary>
internal sealed record DiscoveredHostDocument(string SourceKey, string Path, string RawContent, string ContentHash);

/// <summary>A file that matched a source but was not imported. Only the relative path is ever reported.</summary>
internal sealed record SkippedHostDocument(string SourceKey, string Path, string Reason);

internal sealed record HostDocsDiscoveryResult(IReadOnlyList<DiscoveredHostDocument> Documents, IReadOnlyList<SkippedHostDocument> Skipped);

/// <summary>
/// Finds the documents of one <c>ExposeDocs</c> registration. Pure file-system / manifest-resource reads, no database.
/// Directory sources refuse anything that resolves outside the configured directory and any symbolic link inside it,
/// so a planted link cannot pull in files from elsewhere on the host.
/// </summary>
internal static class HostDocsDiscovery
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".txt" };

    public static HostDocsDiscoveryResult Discover(HostDocsRegistration registration, string contentRoot)
    {
        var documents = new List<DiscoveredHostDocument>();
        var skipped = new List<SkippedHostDocument>();

        foreach (var source in registration.Sources)
        {
            switch (source)
            {
                case HostDocsDirectorySource directory:
                    DiscoverDirectory(directory, registration.Options, contentRoot, documents, skipped);
                    break;
                case HostDocsResourceSource resources:
                    DiscoverResources(resources, registration.Options, documents, skipped);
                    break;
            }
        }

        var ordered = documents
            .OrderBy(x => x.SourceKey, StringComparer.Ordinal)
            .ThenBy(x => x.Path, StringComparer.Ordinal)
            .ToList();

        return new HostDocsDiscoveryResult(ordered, skipped);
    }

    private static void DiscoverDirectory(
        HostDocsDirectorySource source,
        HostDocsOptions options,
        string contentRoot,
        List<DiscoveredHostDocument> documents,
        List<SkippedHostDocument> skipped)
    {
        var root = Path.GetFullPath(Path.IsPathRooted(source.Path) ? source.Path : Path.Combine(contentRoot, source.Path));
        if (!Directory.Exists(root))
        {
            // Fail loudly: a missing directory would otherwise archive every previously imported document.
            throw new InvalidOperationException($"ExposeDocs: directory '{source.Path}' does not exist under the content root.");
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(source.Globs);
        matcher.AddExcludePatterns(options.ExcludeGlobs);

        var matches = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(root)));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        foreach (var match in matches.Files)
        {
            var relative = match.Path.Replace('\\', '/');
            if (!AllowedExtensions.Contains(Path.GetExtension(relative)))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, relative));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                skipped.Add(new SkippedHostDocument(source.Key, relative, "resolves outside the configured directory"));
                continue;
            }

            if (ContainsLink(fullPath, root))
            {
                skipped.Add(new SkippedHostDocument(source.Key, relative, "is or is under a symbolic link"));
                continue;
            }

            var file = new FileInfo(fullPath);
            if (file.Length > options.MaxDocumentBytes)
            {
                skipped.Add(new SkippedHostDocument(source.Key, relative, $"exceeds MaxDocumentBytes ({options.MaxDocumentBytes})"));
                continue;
            }

            if (relative.Length > ProjectImportedDocument.MaxPathLength)
            {
                skipped.Add(new SkippedHostDocument(source.Key, relative[..80] + "…", $"path is longer than {ProjectImportedDocument.MaxPathLength} characters"));
                continue;
            }

            documents.Add(Create(source.Key, relative, File.ReadAllBytes(fullPath)));
        }
    }

    private static void DiscoverResources(
        HostDocsResourceSource source,
        HostDocsOptions options,
        List<DiscoveredHostDocument> documents,
        List<SkippedHostDocument> skipped)
    {
        var excludes = new Matcher(StringComparison.OrdinalIgnoreCase);
        excludes.AddInclude("**/*");
        excludes.AddExcludePatterns(options.ExcludeGlobs);

        // A LogicalName built from %(RecursiveDir) carries backslashes on Windows builds — compare on '/' form.
        var prefix = source.Prefix.Replace('\\', '/');
        var names = source.Assembly
            .GetManifestResourceNames()
            .Where(x => x.Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        foreach (var name in names)
        {
            var relative = name.Replace('\\', '/')[prefix.Length..].TrimStart('/');
            if (relative.Length == 0 || !AllowedExtensions.Contains(Path.GetExtension(relative)))
            {
                continue;
            }

            if (relative.Split('/').Contains(".."))
            {
                skipped.Add(new SkippedHostDocument(source.Key, relative, "contains a '..' segment"));
                continue;
            }

            if (relative.Length > ProjectImportedDocument.MaxPathLength)
            {
                skipped.Add(new SkippedHostDocument(source.Key, relative[..80] + "…", $"path is longer than {ProjectImportedDocument.MaxPathLength} characters"));
                continue;
            }

            if (options.ExcludeGlobs.Count > 0 && !excludes.Match(relative).HasMatches)
            {
                continue;
            }

            using var stream = source.Assembly.GetManifestResourceStream(name);
            if (stream == null)
            {
                continue;
            }

            if (stream.Length > options.MaxDocumentBytes)
            {
                skipped.Add(new SkippedHostDocument(source.Key, relative, $"exceeds MaxDocumentBytes ({options.MaxDocumentBytes})"));
                continue;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            documents.Add(Create(source.Key, relative, buffer.ToArray()));
        }
    }

    // The file itself and every directory between it and the configured root must be real entries, not links.
    private static bool ContainsLink(string fullPath, string root)
    {
        if (new FileInfo(fullPath).LinkTarget != null)
        {
            return true;
        }

        var directory = Path.GetDirectoryName(fullPath);
        while (directory != null && directory.Length > root.Length)
        {
            if (new DirectoryInfo(directory).LinkTarget != null)
            {
                return true;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    private static DiscoveredHostDocument Create(string sourceKey, string relativePath, byte[] bytes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var content = new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');

        return new DiscoveredHostDocument(sourceKey, relativePath, content, hash);
    }
}
