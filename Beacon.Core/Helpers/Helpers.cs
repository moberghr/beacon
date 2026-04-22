using System.Text.Json;

namespace Beacon.Core.Helpers;

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
    
    public static string ToShortDateWithoutYear(this DateTime value)
    {
        return value.ToString("dd.MM.");
    }
    
    public static string GetSubstring(this string value, int length)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= length)
        {
            return value;
        }
        else
        {
            return value.Substring(0, length - 3) + "...";
        }
    }

    public static DateTime? GetCronNextAt(this string cron)
    {
        if (!Cronos.CronExpression.TryParse(cron, out var expression))
        {
            return null;
        }

        return expression?.GetNextOccurrence(DateTime.UtcNow, true);
    }

    public static string GetCronDescription(this string cron)
    {
        if (!Cronos.CronExpression.TryParse(cron, out var expression))
        {
            return string.Empty;
        }

        return CronExpressionDescriptor.ExpressionDescriptor.GetDescription(cron, new CronExpressionDescriptor.Options()
        {
            DayOfWeekStartIndexZero = false,
            Use24HourTimeFormat = true,
            ThrowExceptionOnParseError = false
        });
    }
}

public class BaseResponse
{
    public bool Success { get; set; }
    
    public string Message { get; set; }
}