using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Projects;

namespace Beacon.AI.Handlers.Projects;

/// <summary>
/// Applies an instruction to one stored documentation section through the LLM provider. A section whose stored text can
/// list values discovered in the data (<see cref="ProjectDocSectionTypes.ListingValues"/>) is row data: the request is
/// logged by the row-data egress log, and while row data may not reach the provider (<c>Beacon:Mcp:ForbidRowDataToLlm</c>)
/// such a section is refused — regenerating the documentation is the way to change it then.
/// </summary>
internal sealed class InstructDocumentationHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ILlmProvider llmProvider,
    ILlmRowDataEgress rowDataEgress,
    IOptions<McpDeploymentOptions> deploymentOptions)
    : IRequestHandler<InstructDocumentationCommand, InstructDocumentationResult>
{
    /// <summary>What a caller is told when the row-data lock keeps a value-listing section from the provider.</summary>
    internal const string LockedSectionMessage =
        "This section can list values discovered in the data, which this deployment does not send to the LLM provider. Regenerate the documentation to change it.";

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
                    x.SectionType,
                    x.Documentation.ProjectId
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (section == null)
        {
            throw new InvalidOperationException("Documentation section not found.");
        }

        var listsValues = section.SectionType.ListsValues();
        if (listsValues && deploymentOptions.Value.ForbidRowDataToLlm)
        {
            throw new InvalidOperationException(LockedSectionMessage);
        }

        var systemPrompt = BuildSystemPrompt(section.Title, section.SectionType);
        var userPrompt = BuildUserPrompt(section.Content, request.Instruction);

        var llmRequest = new LlmRequest
        {
            SystemPrompt = systemPrompt,
            Messages = [new ChatMessage(ConversationRole.User, userPrompt)],
            Temperature = 0.3m,
            MaxTokens = 8192,
            Operation = LlmOperations.DocumentationInstruct
        };

        var provider = rowDataEgress.Track(
            llmProvider,
            new LlmRowDataScope(section.ProjectId, [], listsValues ? [LlmRowDataCategories.StoredDocumentation] : []));
        var response = await provider.CompleteAsync(llmRequest, cancellationToken);

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
