using Beacon.Core.Models;

namespace Beacon.AI.Services.Mcp;

/// <summary>
/// The LLM calls one <c>ask</c> call may still make (<c>Beacon:Mcp:Ceilings:AskMaxLlmCalls</c>). Created per call and
/// consulted before every LLM call on the path: a step that needs a call takes one first, and a step that cannot get one
/// is skipped or, before any SQL was generated, fails the call with <see cref="AskLlmBudgetExceededException"/>.
/// Thread-safe, so concurrent self-consistency candidates share it.
/// </summary>
public sealed class AskLlmBudget
{
    private readonly Lock _lock = new();
    private int _used;

    public AskLlmBudget(int maxCalls)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCalls, 1);

        MaxCalls = maxCalls;
    }

    public int MaxCalls { get; }

    public int Used
    {
        get
        {
            lock (_lock)
            {
                return _used;
            }
        }
    }

    public int Remaining
    {
        get
        {
            lock (_lock)
            {
                return MaxCalls - _used;
            }
        }
    }

    /// <summary>Takes one call when, after it, at least <paramref name="keepInReserve"/> calls are left.</summary>
    public bool TryConsume(int keepInReserve = 0)
    {
        lock (_lock)
        {
            if (MaxCalls - _used - 1 < keepInReserve)
            {
                return false;
            }

            _used++;

            return true;
        }
    }

    /// <summary>Takes as many of <paramref name="count"/> calls as are left and returns how many it took.</summary>
    public int ConsumeUpTo(int count)
    {
        lock (_lock)
        {
            var granted = Math.Clamp(count, 0, MaxCalls - _used);
            _used += granted;

            return granted;
        }
    }

    /// <summary>Takes one call, or throws <see cref="AskLlmBudgetExceededException"/> when none is left.</summary>
    public void Consume()
    {
        if (!TryConsume())
        {
            throw new AskLlmBudgetExceededException(MaxCalls);
        }
    }
}

/// <summary>The <c>ask</c> call spent its LLM-call budget before it could answer. The message carries no value.</summary>
public sealed class AskLlmBudgetExceededException(int maxCalls)
    : BeaconException($"ask reached its limit of {maxCalls} LLM call{(maxCalls == 1 ? "" : "s")} before it could answer; narrow the question or ask again.")
{
    public int MaxCalls { get; } = maxCalls;
}
