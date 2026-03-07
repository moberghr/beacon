using Semantico.Core.Data.Entities.Projects;

namespace Semantico.AI.Services.GitHub;

public interface ICodeAnalyzer
{
    string Language { get; }
    bool CanAnalyze(string filePath);
    List<CodeReference> AnalyzeFile(string filePath, string content);
}
