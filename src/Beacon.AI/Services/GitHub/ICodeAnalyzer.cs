using Beacon.Core.Data.Entities.Projects;

namespace Beacon.AI.Services.GitHub;

public interface ICodeAnalyzer
{
    string Language { get; }
    bool CanAnalyze(string filePath);
    List<CodeReference> AnalyzeFile(string filePath, string content);
}
