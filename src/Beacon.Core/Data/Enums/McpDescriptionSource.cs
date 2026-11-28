namespace Beacon.Core.Data.Enums;

/// <summary>Who proposed the description a <see cref="Entities.McpDescriptionItem"/> records; a person applied it either way.</summary>
public enum McpDescriptionSource
{
    /// <summary>A person applied a patch a person or the learning job proposed.</summary>
    Curated = 1,

    /// <summary>A person applied a patch the description generator proposed.</summary>
    Generated = 2
}
