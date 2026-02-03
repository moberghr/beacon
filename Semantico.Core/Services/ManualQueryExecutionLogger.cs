using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;

namespace Semantico.Core.Services;

public interface IManualQueryExecutionLogger
{
    Task LogQueryExecutionAsync(
        string queryText,
        int resultCount,
        double executionTimeMs,
        bool success,
        int? dataSourceId = null,
        string? executionContext = null,
        string? errorMessage = null,
        string? userId = null,
        CancellationToken cancellationToken = default);
}

internal sealed class ManualQueryExecutionLogger(
    IDbContextFactory<SemanticoContext> contextFactory,
    ILogger<ManualQueryExecutionLogger> logger) : IManualQueryExecutionLogger
{
    public async Task LogQueryExecutionAsync(
        string queryText,
        int resultCount,
        double executionTimeMs,
        bool success,
        int? dataSourceId = null,
        string? executionContext = null,
        string? errorMessage = null,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            var log = new ManualQueryExecutionLog
            {
                UserId = userId,
                QueryText = queryText,
                ResultCount = resultCount,
                ExecutionTimeMs = executionTimeMs,
                Success = success,
                DataSourceId = dataSourceId,
                ExecutionContext = executionContext,
                ErrorMessage = errorMessage
            };

            context.ManualQueryExecutionLogs.Add(log);
            await context.SaveChangesAsync(cancellationToken);

            logger.LogDebug(
                "Logged manual query execution: Context={ExecutionContext}, DataSource={DataSourceId}, Success={Success}, ResultCount={ResultCount}, ExecutionTimeMs={ExecutionTimeMs}",
                executionContext, dataSourceId, success, resultCount, executionTimeMs);
        }
        catch (Exception ex)
        {
            // Don't throw - logging should not break the main flow
            logger.LogWarning(ex, "Failed to log manual query execution");
        }
    }
}
