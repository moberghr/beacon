namespace Beacon.Core.Authorization;

/// <summary>What a caller wants to do with a project's resources; <see cref="ProjectAccessScope.For"/> answers per action.</summary>
public enum ProjectAction
{
    /// <summary>See a resource and its data: SQL text, metadata including sample values, stored results, documentation.</summary>
    Read = 1,

    /// <summary>Execute stored SQL: saved-query previews, data-quality evaluation, saved-query MCP tools.</summary>
    Run = 2,

    /// <summary>Ad-hoc or draft SQL and LLM work.</summary>
    Query = 3,

    /// <summary>Create or update resources whose data sources lie in the project.</summary>
    Write = 4,

    /// <summary>Delete or archive, override another user's lock, force a metadata refresh.</summary>
    Manage = 5,

    /// <summary>Grant, change and revoke membership; link data sources into the project. Interactive sessions only.</summary>
    ManageMembers = 6,

    /// <summary>
    /// Curate the grounding prompts are built from: review learned patterns, edit generated documentation, apply or reject
    /// documentation patches, verify schema relationships. A ProjectAdmin membership (or an Admin) only, never an open
    /// grant; interactive sessions only — never an API key, an MCP caller or the owner of scheduled work.
    /// </summary>
    Curate = 7
}
