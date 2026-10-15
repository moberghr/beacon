using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Services.Security;

/// <summary>
/// The secret the first-run setup request must present before Beacon creates the initial super admin. It is
/// <see cref="UserManagementOptions.SetupToken"/> when configured; otherwise a random token generated once per process,
/// which <see cref="AnnounceWhileFirstRun"/> writes to the console (standard error) so only an operator with access to
/// the process output can use it. The token never goes through <see cref="ILogger"/>: log pipelines are shipped and
/// retained far more widely than a process's console.
/// </summary>
internal sealed class FirstRunSetupToken
{
    /// <summary>The shortest configured token accepted at startup.</summary>
    public const int MinimumConfiguredLength = 32;

    private readonly string _token;
    private readonly ILogger<FirstRunSetupToken> _logger;
    private readonly TextWriter _console;
    private int _announced;

    public FirstRunSetupToken(BeaconConfiguration configuration, ILogger<FirstRunSetupToken> logger)
        : this(configuration, logger, Console.Error)
    {
    }

    internal FirstRunSetupToken(BeaconConfiguration configuration, ILogger<FirstRunSetupToken> logger, TextWriter console)
    {
        var configured = configuration.UserManagement.SetupToken?.Trim();
        IsGenerated = string.IsNullOrEmpty(configured);
        _token = IsGenerated
            ? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))
            : configured!;
        _logger = logger;
        _console = console;
    }

    /// <summary>True when no token was configured and this process generated one.</summary>
    public bool IsGenerated { get; }

    /// <summary>The constant-time comparison <see cref="Verify"/> uses; replaceable only so tests can observe it.</summary>
    internal Func<string?, string, bool> Comparer { get; init; } = FixedTimeSecretComparer.Matches;

    /// <summary>Constant-time check of the token presented by the setup request.</summary>
    public bool Verify(string? presented)
    {
        return Comparer(presented?.Trim(), _token);
    }

    /// <summary>
    /// Writes the generated token to the console, once per process, and logs (without the token) where it went.
    /// Callers invoke it only after observing that first-run setup is still open, so the token is never written for an
    /// installation that is already set up. A configured token is never written anywhere.
    /// </summary>
    public void AnnounceWhileFirstRun()
    {
        if (Interlocked.Exchange(ref _announced, 1) == 1)
        {
            return;
        }

        if (!IsGenerated)
        {
            _logger.LogInformation(
                "First-run setup is open: no super admin exists yet. The setup page needs the configured setup token.");
            return;
        }

        _console.WriteLine($"Beacon first-run setup token (enter it on the setup page): {_token}");
        _console.Flush();

        _logger.LogWarning(
            "First-run setup is open: no super admin exists yet. The setup token was written to this process's console " +
            "(standard error), not to the log. Configure Beacon:UserManagement:SetupToken to choose it instead.");
    }
}
