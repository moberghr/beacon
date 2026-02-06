using Semantico.Core.Adapters.Mail;
using Semantico.Core.Authentication;
using Semantico.Core.Authorization;
using Semantico.Core.Models;
using Semantico.Core.Worker;

namespace Semantico.Core;

public class SemanticoConfiguration
{
    public string ConnectionStringName { get; set; } = nameof(Data.SemanticoContext);

    /// <summary>
    /// Base URL of the Semantico admin UI (e.g., https://your-domain.com/semantico)
    /// Used for generating links in notifications.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Enables AI-powered features in the UI (documentation generation, smart alerts).
    /// Requires LLM configuration in appsettings.json.
    /// </summary>
    public bool UseAI { get; set; } = false;

    /// <summary>
    /// Controls database metadata loading behavior. Useful for large databases with hundreds of tables.
    /// </summary>
    public MetadataLoadingOptions MetadataLoading { get; set; } = new();

    /// <summary>
    /// Authorization configuration
    /// </summary>
    public AuthorizationOptions Authorization { get; set; } = new();

    /// <summary>
    /// Authentication configuration
    /// </summary>
    public AuthenticationOptions Authentication { get; set; } = new();

    /// <summary>
    /// User management configuration
    /// </summary>
    public UserManagementOptions UserManagement { get; set; } = new();

    public void AddSemanticoScheduler<T>() where T : class, ISemanticoScheduler
    {
        SemanticoScheduler = typeof(T);
    }

    public void AddEmailAdapter<T>() where T : class, IEmailAdapter
    {
        EmailAdapter = typeof(T);
    }

    public void AddAuthorizationProvider<T>() where T : class, ISemanticoAuthorizationProvider
    {
        Authorization.ProviderType = typeof(T);
    }

    public void AddAuthenticationProvider<T>() where T : class, ISemanticoAuthenticationProvider
    {
        Authentication.ProviderType = typeof(T);
    }

    /// <summary>
    /// Enables user management with optional configuration.
    /// This feature allows managing internal users with passwords stored in Semantico,
    /// as well as pre-registering external users for JWT/OAuth authentication.
    /// </summary>
    public void EnableUserManagement(Action<UserManagementOptions>? configure = null)
    {
        UserManagement.Enabled = true;
        configure?.Invoke(UserManagement);
    }

    internal Type? SemanticoScheduler { get; set; }

    internal Type? EmailAdapter { get; set; }

    internal void ValidateCore()
    {
        if (SemanticoScheduler == null)
        {
            throw new SemanticoException($"Implementation of ISemanticoScheduler is required.");
        }
    }
}
