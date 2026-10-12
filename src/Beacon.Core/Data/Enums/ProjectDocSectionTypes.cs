namespace Beacon.Core.Data.Enums;

/// <summary>What the stored text of a documentation section can hold.</summary>
public static class ProjectDocSectionTypes
{
    /// <summary>
    /// The sections documentation generation hands discovered column values to — business domains, data model and
    /// glossary — whose stored text can therefore list values read from the data. A prompt that sends their stored text
    /// carries row data to the LLM provider, and none is sent while row data may not reach it
    /// (<c>Beacon:Mcp:ForbidRowDataToLlm</c>).
    /// </summary>
    public static IReadOnlyList<ProjectDocSectionType> ListingValues { get; } =
    [
        ProjectDocSectionType.BusinessDomains,
        ProjectDocSectionType.DataModel,
        ProjectDocSectionType.Glossary
    ];

    /// <summary>True when the stored text of a <paramref name="sectionType"/> section can list discovered values.</summary>
    public static bool ListsValues(this ProjectDocSectionType sectionType)
    {
        return sectionType is ProjectDocSectionType.BusinessDomains or ProjectDocSectionType.DataModel or ProjectDocSectionType.Glossary;
    }
}
