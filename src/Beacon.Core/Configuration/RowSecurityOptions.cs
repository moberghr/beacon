using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>
/// Whether this deployment enforces row-level security on data sources, bound from <c>Beacon:RowSecurity</c>. The switch
/// is a deployment lock and off by default; it cannot be turned on in this release (<see cref="RowSecurityOptionsValidator"/>).
/// </summary>
public sealed class RowSecurityOptions
{
    public const string SectionName = "Beacon:RowSecurity";

    /// <summary>Enforces row policies on data sources. Default false.</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Refuses the lock at startup while row-level security cannot be enforced, so no deployment believes its rows are filtered
/// when they are not.
/// </summary>
internal sealed class RowSecurityOptionsValidator : IValidateOptions<RowSecurityOptions>
{
    public ValidateOptionsResult Validate(string? name, RowSecurityOptions options)
    {
        return options.Enabled
            ? ValidateOptionsResult.Fail(
                $"{RowSecurityOptions.SectionName}:{nameof(RowSecurityOptions.Enabled)} cannot be turned on: row-level security is not available in this release.")
            : ValidateOptionsResult.Success;
    }
}
