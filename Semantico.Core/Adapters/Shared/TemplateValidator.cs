using System.Text.Json;
using System.Text.RegularExpressions;

namespace Semantico.Core.Adapters.Shared;

/// <summary>
/// Validates body templates for recipients
/// </summary>
public static partial class TemplateValidator
{
    [GeneratedRegex(@"\{\{([^}]+)\}\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    /// <summary>
    /// Validates a body template
    /// </summary>
    /// <returns>ValidationResult with success status and error message if invalid</returns>
    public static ValidationResult Validate(string? template)
    {
        // Empty or null templates are valid (will use default)
        if (string.IsNullOrWhiteSpace(template))
        {
            return ValidationResult.Success();
        }

        // First, replace all placeholders with dummy values to check JSON structure
        var testTemplate = ReplacePlaceholdersWithDummyValues(template);

        // Try to parse as JSON to validate structure
        try
        {
            // Attempt to parse as JSON
            using var doc = JsonDocument.Parse(testTemplate);

            // If parsing succeeds, it's valid JSON
            return ValidationResult.Success();
        }
        catch (JsonException ex)
        {
            // If it's not valid JSON, check if it's intentionally plain text
            // (some webhooks might accept non-JSON payloads)
            if (template.TrimStart().StartsWith("{") || template.TrimStart().StartsWith("["))
            {
                // Looks like JSON but has syntax errors
                return ValidationResult.Failure($"Invalid JSON syntax: {ex.Message}");
            }

            // It's plain text or other format - allow it but warn
            return ValidationResult.SuccessWithWarning("Template is not JSON. Ensure your webhook accepts this format.");
        }
    }

    /// <summary>
    /// Validates a template and checks for unknown placeholders
    /// </summary>
    public static ValidationResult ValidateWithPlaceholderCheck(string? template)
    {
        var result = Validate(template);
        if (!result.IsValid || string.IsNullOrWhiteSpace(template))
        {
            return result;
        }

        // Check for unknown placeholders
        var unknownPlaceholders = FindUnknownPlaceholders(template);
        if (unknownPlaceholders.Any())
        {
            var placeholderList = string.Join(", ", unknownPlaceholders);
            return ValidationResult.SuccessWithWarning(
                $"Unknown placeholders found: {placeholderList}. These will not be replaced.");
        }

        return result;
    }

    private static string ReplacePlaceholdersWithDummyValues(string template)
    {
        var result = template;
        var matches = PlaceholderRegex().Matches(template);

        foreach (Match match in matches)
        {
            var placeholder = match.Value;
            var placeholderName = match.Groups[1].Value.Trim().ToLowerInvariant();

            // Replace with appropriate dummy values based on expected type
            var dummyValue = placeholderName switch
            {
                var p when p.Contains("record") => "[]",
                var p when p.Contains("count") || p.Contains("id") => "0",
                var p when p.Contains("time") => "0.0",
                var p when p.Contains("timedout") || p.Contains("anomaly") => "false",
                _ => "\"test\""
            };

            result = result.Replace(placeholder, dummyValue);
        }

        return result;
    }

    private static List<string> FindUnknownPlaceholders(string template)
    {
        var knownPlaceholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Timestamp", "Source", "SubscriptionName", "SubscriptionId",
            "DataSourceName", "SqlQuery", "TotalRecords", "ExecutionTimeMs",
            "PreviousResultCount", "TimedOut", "Records", "Records_Pretty",
            "NotificationUrl",
            "Anomaly.IsAnomaly", "Anomaly.Severity", "Anomaly.Explanation",
            "Anomaly"
        };

        var unknownPlaceholders = new List<string>();
        var matches = PlaceholderRegex().Matches(template);

        foreach (Match match in matches)
        {
            var placeholder = match.Groups[1].Value.Trim();
            if (!knownPlaceholders.Contains(placeholder))
            {
                unknownPlaceholders.Add(placeholder);
            }
        }

        return unknownPlaceholders.Distinct().ToList();
    }
}

/// <summary>
/// Result of template validation
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public string? WarningMessage { get; init; }

    public static ValidationResult Success() => new() { IsValid = true };

    public static ValidationResult SuccessWithWarning(string warning) => new()
    {
        IsValid = true,
        WarningMessage = warning
    };

    public static ValidationResult Failure(string error) => new()
    {
        IsValid = false,
        ErrorMessage = error
    };
}
