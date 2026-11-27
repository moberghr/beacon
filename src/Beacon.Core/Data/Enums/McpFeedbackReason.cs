namespace Beacon.Core.Data.Enums;

/// <summary>Why a caller marked a signal's answer wrong, recorded on <see cref="Entities.McpQuerySignal.FeedbackReason"/>.</summary>
public enum McpFeedbackReason
{
    WrongTable = 1,
    WrongColumn = 2,
    WrongJoin = 3,
    WrongFilter = 4,
    WrongAggregation = 5,
    WrongTimeRange = 6,
    MisunderstoodQuestion = 7,
    Other = 8
}
