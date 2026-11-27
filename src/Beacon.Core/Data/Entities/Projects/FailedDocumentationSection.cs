namespace Beacon.Core.Data.Entities.Projects;

/// <summary>
/// What a documentation section whose generation failed holds: a fixed text, never the failure's own message (a provider's
/// error text would otherwise be served to agents as documentation). Such a section is shown on the documentation page,
/// so a person sees what to regenerate, and is left out of everything that serves documentation to agents and models: the
/// Markdown export and <c>get_documentation</c>, <c>search</c>, the table documentation read and the doc-chunk index.
/// </summary>
public static class FailedDocumentationSection
{
    public const string Content = "*Generation failed for this section. Please regenerate.*";
}
