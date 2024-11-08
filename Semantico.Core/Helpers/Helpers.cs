using System.Text.Json;

namespace Semantico.Core.Helpers;

public static class Helpers
{
    /// <summary>
    /// Serialize object to JSON.
    /// </summary>
    public static string? ToJson(this object? value)
    {
        return JsonSerializer.Serialize(value);
    }
}

public class BaseResponse
{
    public bool Success { get; set; }
    
    public string Message { get; set; }
}