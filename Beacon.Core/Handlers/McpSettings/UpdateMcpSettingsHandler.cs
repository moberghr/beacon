using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.McpSettings;

internal sealed class UpdateMcpSettingsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider)
    : IRequestHandler<UpdateMcpSettingsCommand>
{
    public async Task Handle(UpdateMcpSettingsCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.McpSettings.FirstOrDefaultAsync(cancellationToken);

        if (entity == null)
        {
            entity = new Data.Entities.McpSettings();
            context.McpSettings.Add(entity);
        }

        var data = request.Data;
        entity.AskSystemPrompt = data.AskSystemPrompt;
        entity.GlobalInstruction = data.GlobalInstruction;
        entity.GetContextDescription = data.GetContextDescription;
        entity.SearchDescription = data.SearchDescription;
        entity.QueryDescription = data.QueryDescription;
        entity.GetDocumentationDescription = data.GetDocumentationDescription;
        entity.AskDescription = data.AskDescription;
        entity.MaxRowLimit = data.MaxRowLimit;
        entity.EnforceReadOnly = data.EnforceReadOnly;
        entity.EnablePiiDetection = data.EnablePiiDetection;
        entity.CustomPiiPatterns = data.CustomPiiPatterns.Count > 0
            ? JsonSerializer.Serialize(data.CustomPiiPatterns)
            : null;

        // Learning settings
        entity.EnableLearning = data.EnableLearning;
        entity.LearningAutoApproveThreshold = data.LearningAutoApproveThreshold;
        entity.LearningInjectionBudgetChars = data.LearningInjectionBudgetChars;
        entity.LearningSignalRetentionDays = data.LearningSignalRetentionDays;

        await context.SaveChangesAsync(cancellationToken);
        settingsProvider.InvalidateCache();
    }
}

public record UpdateMcpSettingsCommand(McpSettingsData Data) : IRequest;
