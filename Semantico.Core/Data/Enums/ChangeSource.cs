namespace Semantico.Core.Data.Enums;

/// <summary>
/// Identifies the source of a change to a query or subscription.
/// </summary>
public enum ChangeSource
{
    /// <summary>
    /// Change made manually by a user through the UI
    /// </summary>
    User = 1,

    /// <summary>
    /// Change made by an AI Actor during refinement
    /// </summary>
    AiActor = 2,

    /// <summary>
    /// Change imported from an external source
    /// </summary>
    Import = 3
}
