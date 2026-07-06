using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Projects;

namespace Beacon.AI.Handlers.Projects;

internal sealed class InstructDocumentationHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ILlmProvider llmProvider)
    : IRequestHandler<InstructDocumentationCommand, InstructDocumentationResult>
{
    public async Task<InstructDocumentationResult> Handle(
        InstructDocumentationCommand request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var section = await context.ProjectDocumentationSections
            .Where(x => x.Id == request.SectionId)
            .Select(x =>
                new
                {
                    x.Id,
                    x.Title,
                    x.Content,
                    x.SectionType
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (section == null)
        {
            throw new InvalidOperationException("Documentation section not found.");
        }

        var systemPrompt = BuildSystemPrompt(section.Title, section.SectionType);
        var userPrompt = BuildUserPrompt(section.Content, request.Instruction);

        var llmRequest = new LlmRequest
        {
            SystemPrompt = systemPrompt,
            Messages = [new ChatMessage(ConversationRole.User, userPrompt)],
            Temperature = 0.3m,
            MaxTokens = 8192
        };

        var response = await llmProvider.CompleteAsync(llmRequest, cancellationToken);

        return new InstructDocumentationResult(response.Content);
    }

    private static string BuildSystemPrompt(string sectionTitle, ProjectDocSectionType sectionType)
    {
        return $"""
            You are a technical documentation editor for a data platform. You are editing the "{sectionTitle}" section of project documentation.

            Rules:
            - Apply the user's instruction to the current documentation content
            - Preserve the existing markdown structure and formatting
            - Keep Mermaid diagrams intact unless the instruction specifically asks to change them
            - Return ONLY the updated section content — no explanations, no preamble, no "Here is the updated content:"
            - If the instruction asks to remove or archive something, remove it from the content or clearly mark it as deprecated/archived
            - If the instruction asks to add something, integrate it naturally into the existing content
            - Maintain the same level of technical detail and writing style as the original
            """;
    }

    private static string BuildUserPrompt(string currentContent, string instruction)
    {
        return $"""
            ## Current Content

            {currentContent}

            ## Instruction

            {instruction}
            """;
    }
}
