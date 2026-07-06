using Beacon.Core.Data.Entities;
using Beacon.Core.Models.Ai;

namespace Beacon.AI.Services.Ai;

public interface IAiAlertGenerationService
{
    Task<AiAlertConfiguration> GenerateAlertAsync(
        int dataSourceId,
        string naturalLanguageDescription,
        string createdBy,
        AlertGenerationOptions options,
        CancellationToken cancellationToken = default);

    Task<AiAlertConfiguration> RefineAlertAsync(
        int alertConfigurationId,
        string userFeedback,
        string modifiedBy,
        CancellationToken cancellationToken = default);

    Task<bool> ValidateQuerySyntaxAsync(
        int dataSourceId,
        string sql,
        CancellationToken cancellationToken = default);

    Task<AiAlertConfiguration> ApproveAndActivateAlertAsync(
        int alertConfigurationId,
        string approvedBy,
        CancellationToken cancellationToken = default);
}
