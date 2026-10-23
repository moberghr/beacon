namespace Beacon.Core.Models;

/// <summary>Upper bounds on user-supplied input the backend accepts.</summary>
public static class InputLimits
{
    /// <summary>Longest corrected SQL a feedback verdict may carry, in characters.</summary>
    public const int FeedbackCorrectedSqlMaxLength = 100_000;

    /// <summary>Longest note a feedback verdict may carry, in characters.</summary>
    public const int FeedbackNoteMaxLength = 2_000;

    /// <summary>Most items a list request returns when it names a size (a <c>take</c> or history limit).</summary>
    public const int MaxListTake = 200;
}
