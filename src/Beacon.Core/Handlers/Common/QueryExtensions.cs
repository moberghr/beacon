using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Common;

/// <summary>
/// Small composition helpers used by MediatR handlers. Keeps the
/// "find by id or throw" pattern out of every handler body.
/// </summary>
public static class QueryExtensions
{
    /// <summary>
    /// Materializes the first matching row or throws
    /// <see cref="InvalidOperationException"/> with a "{entityName} {id} not found." message.
    /// Use after a chained <c>.Where(...)</c> so the predicate stays out of the terminal call (§3.4).
    /// </summary>
    public static async Task<T> FirstOrThrowAsync<T>(
        this IQueryable<T> query,
        string entityName,
        object id,
        CancellationToken cancellationToken)
        where T : class
    {
        var entity = await query.FirstOrDefaultAsync(cancellationToken);
        return entity ?? throw new InvalidOperationException($"{entityName} {id} not found.");
    }
}
