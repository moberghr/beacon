using Beacon.Core.Data.Entities.Projects;

namespace Beacon.Core.HostDocs;

/// <summary>One <c>ExposeDocs</c> call, validated and captured at registration time. Held as a singleton.</summary>
internal sealed class HostDocsRegistration
{
    public HostDocsRegistration(HostDocsOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProjectName))
        {
            throw new InvalidOperationException("ExposeDocs requires ProjectName (the project the documents attach to).");
        }

        if (options.Sources.Count == 0)
        {
            throw new InvalidOperationException("ExposeDocs requires at least one FromDirectory or FromEmbeddedResources source.");
        }

        if (options.MaxDocumentBytes <= 0)
        {
            throw new InvalidOperationException("ExposeDocs: MaxDocumentBytes must be positive.");
        }

        var tooLong = options.Sources
            .Where(x => x.Key.Length > ProjectImportedDocument.MaxSourceKeyLength)
            .Select(x => x.Key)
            .FirstOrDefault();

        if (tooLong != null)
        {
            throw new InvalidOperationException($"ExposeDocs: source key '{tooLong}' is longer than {ProjectImportedDocument.MaxSourceKeyLength} characters; use a shorter path or prefix.");
        }

        var duplicate = options.Sources
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .FirstOrDefault();

        if (duplicate != null)
        {
            throw new InvalidOperationException($"ExposeDocs: source '{duplicate}' is declared twice.");
        }

        Options = options;
        ProjectName = options.ProjectName.Trim();
    }

    public HostDocsOptions Options { get; }

    public string ProjectName { get; }

    public IReadOnlyList<HostDocsSource> Sources => Options.Sources;
}
