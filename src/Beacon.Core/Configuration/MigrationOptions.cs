using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>
/// Whether this deployment runs data migrations, and how many rows one may copy, bound from <c>Beacon:Migrations</c>.
/// Migrations are the one Beacon feature that writes to a connected database, so they are off unless the host turns them
/// on. The switch is a deployment lock: while it is off the migration endpoints are not mapped, the shell hides the
/// feature, no job is created or run whoever asks (an Admin included, and any in-process caller), and no data source can
/// be marked as a migration write target.
/// </summary>
public sealed class MigrationOptions
{
    public const string SectionName = "Beacon:Migrations";

    /// <summary>The default of <see cref="MaxRows"/>.</summary>
    public const int DefaultMaxRows = 100_000;

    /// <summary>The largest <see cref="MaxRows"/> accepted. A run holds every row it copies in memory.</summary>
    public const int MaxAllowedRows = 10_000_000;

    /// <summary>Maps the migration endpoints and allows migrations to run. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The most rows one migration run may read and write — its own budget, in place of <c>Beacon:Query:MaxExecutionRows</c>,
    /// which bounds every other read. The cap is written into the source SQL and the read stops one row past it; a larger
    /// source fails the run before anything is written, it is never cut short. Default 100,000; between 1 and
    /// <see cref="MaxAllowedRows"/>.
    /// </summary>
    public int MaxRows { get; set; } = DefaultMaxRows;
}

internal sealed class MigrationOptionsValidator : IValidateOptions<MigrationOptions>
{
    public ValidateOptionsResult Validate(string? name, MigrationOptions options)
    {
        return options.MaxRows is >= 1 and <= MigrationOptions.MaxAllowedRows
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{MigrationOptions.SectionName}:{nameof(MigrationOptions.MaxRows)} must be between 1 and {MigrationOptions.MaxAllowedRows}.");
    }
}
