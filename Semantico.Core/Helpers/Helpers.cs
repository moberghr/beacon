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
    
    public static string ToShortDate(this DateTime? value)
    {
        return value == null
            ? string.Empty 
            : value.Value.ToShortDate();
    }

    public static string ToShortDate(this DateTime value)
    {
        return value.ToString("dd.MM.yyyy");
    }
}

public class BaseResponse
{
    public bool Success { get; set; }
    
    public string Message { get; set; }
}