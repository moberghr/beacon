using Semantico.Core.Helpers;
using Semantico.Core.Models.Queries;
using Semantico.Core.Models.Subscriptions;

namespace Semantico.Core.Services.Shared;

/// <summary>
/// Shared service for resolving and compiling query parameters
/// Used by both QueryService and MigrationService
/// </summary>
internal class ParameterResolver
{
    /// <summary>
    /// Compiles SQL by substituting parameter placeholders with actual values
    /// </summary>
    public string CompileSql(string sqlTemplate, List<SubscriptionParamaterData>? parameters)
    {
        return QueryHelper.CompileSql(sqlTemplate, parameters);
    }

    /// <summary>
    /// Extracts parameter values from a request by matching parameter names
    /// </summary>
    public List<SubscriptionParamaterData> ExtractParameters(
        List<QueryStepParameterData> stepParameters,
        List<ParameterValue>? providedValues)
    {
        if (providedValues == null || !providedValues.Any())
            return new List<SubscriptionParamaterData>();

        return stepParameters.Select(p =>
        {
            var value = providedValues.FirstOrDefault(param => param.Name == p.Name)?.Value ?? "";
            return new SubscriptionParamaterData
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
