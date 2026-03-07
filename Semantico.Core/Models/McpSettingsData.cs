namespace Semantico.Core.Models;

public class McpSettingsData
{
    public string? AskSystemPrompt { get; set; }
    public string? GlobalInstruction { get; set; }
    public string? ListDataSourcesDescription { get; set; }
    public string? QueryDescription { get; set; }
    public string? GetDocumentationDescription { get; set; }
    public string? AskDescription { get; set; }
    public int MaxRowLimit { get; set; } = 1000;
    public bool EnforceReadOnly { get; set; } = true;
    public bool EnablePiiDetection { get; set; } = true;
    public List<string> CustomPiiPatterns { get; set; } = [];
}
