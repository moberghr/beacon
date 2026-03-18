using MediatR;
using Semantico.AI.Services.Documentation;
using Semantico.Core.Handlers.Projects;

namespace Semantico.AI.Handlers.Projects;

internal sealed class ExportProjectDocumentationHandler(
    IProjectDocumentationService documentationService) : IRequestHandler<ExportProjectDocumentationCommand, ExportProjectDocumentationResult>
{
    public async Task<ExportProjectDocumentationResult> Handle(
        ExportProjectDocumentationCommand request,
        CancellationToken cancellationToken)
    {
        if (request.AsHtml)
        {
            var html = await documentationService.ExportToHtmlAsync(request.DocumentationId, cancellationToken);
            return new ExportProjectDocumentationResult(html, "text/html", "documentation.html");
        }

        var markdown = await documentationService.ExportToMarkdownAsync(request.DocumentationId, cancellationToken);
        return new ExportProjectDocumentationResult(markdown, "text/markdown", "documentation.md");
    }
}
