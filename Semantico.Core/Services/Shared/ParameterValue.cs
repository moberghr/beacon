namespace Semantico.Core.Services.Shared;

/// <summary>
/// Parameter value for query execution (shared across Query and Migration services)
/// </summary>
public class ParameterValue
{
    public string Name { get; set; } = null!;
    public string Value { get; set; } = null!;
}
