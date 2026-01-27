namespace Semantico.Core.Models.Providers;

public class QueryValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Suggested fixes or improvements
    /// </summary>
    public List<string> Suggestions { get; set; } = new();
}
