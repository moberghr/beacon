namespace Semantico.Core.Authentication.Providers;

/// <summary>
/// Default authentication provider that always fails.
/// Used when no custom authentication provider is configured.
/// </summary>
internal sealed class DefaultAuthenticationProvider : ISemanticoAuthenticationProvider
{
    public Task<AuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(AuthenticationResult.Failed(
            "Authentication is not configured. Please implement ISemanticoAuthenticationProvider."));
    }

    public Task<bool> ValidateSessionAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
