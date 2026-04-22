namespace Beacon.Core.Models.Ai;

public class GenerationOptions
{
    public string? Title { get; set; }
    public int MaxTables { get; set; } = 50;
    public int SampleRowsPerTable { get; set; } = 10;
    public bool IncludeSampleData { get; set; } = true;
    public bool IncludeRelationships { get; set; } = true;
    public bool IncludeIndexes { get; set; } = true;
    public decimal Temperature { get; set; } = 0.3m;
    public int MaxTokens { get; set; } = 4096;
    public List<string>? SpecificTables { get; set; }
    public List<string>? ExcludedTables { get; set; }
}
