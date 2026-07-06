using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Models.Subscriptions;

namespace Beacon.Core.Services.Shared;

/// <summary>
/// Shared service for resolving and compiling query parameters
/// Used by both QueryService and MigrationService
/// </summary>
internal class ParameterResolver
{
    /// <summary>
    /// Prepares a parameterized SQL query with proper SQL injection protection.
    /// Converts custom placeholders to database-compatible parameter names.
    /// </summary>
    public (string Sql, Dictionary<string, object?> Parameters) PrepareParameterizedQuery(
        string sqlTemplate,
        List<SubscriptionParameterData>? parameters)
    {
        return QueryHelper.PrepareParameterizedQuery(sqlTemplate, parameters);
    }

    /// <summary>
    /// Compiles SQL by substituting parameter placeholders with actual values.
    /// DEPRECATED: Use PrepareParameterizedQuery instead for better SQL injection protection.
    /// </summary>
    [Obsolete("Use PrepareParameterizedQuery instead for better SQL injection protection")]
    public string CompileSql(string sqlTemplate, List<SubscriptionParameterData>? parameters)
    {
        return QueryHelper.CompileSql(sqlTemplate, parameters);
    }

    /// <summary>
    /// Extracts parameter values from a request by matching parameter names
    /// </summary>
    public List<SubscriptionParameterData> ExtractParameters(
        List<QueryStepParameterData> stepParameters,
        List<ParameterValue>? providedValues)
    {
        if (providedValues == null || !providedValues.Any())
            return new List<SubscriptionParameterData>();

        return stepParameters.Select(p =>
        {
            var value = providedValues.FirstOrDefault(param => param.Name == p.Name)?.Value ?? "";
            return new SubscriptionParameterData
            {
                QueryPlaceholder = p.Name,
                Value = value
            };
        }).ToList();
    }

    /// <summary>
    /// Validates that all required parameters are provided
    /// </summary>
    public ValidationResult ValidateParameters(
        List<QueryStepParameterData> requiredParameters,
        List<ParameterValue>? providedValues)
    {
        var providedNames = providedValues?.Select(p => p.Name).ToHashSet() ?? new HashSet<string>();
        var missingParameters = requiredParameters
            .Where(p => !providedNames.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();

        if (missingParameters.Any())
        {
            return ValidationResult.Failure($"Missing required parameters: {string.Join(", ", missingParameters)}");
        }

        return ValidationResult.Success();
    }
}

/// <summary>
/// Result of parameter validation
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; set; }
    public string? ErrorMessage { get; set; }

    public static ValidationResult Success() => new() { IsValid = true };
    public static ValidationResult Failure(string errorMessage) => new() { IsValid = false, ErrorMessage = errorMessage };
}
