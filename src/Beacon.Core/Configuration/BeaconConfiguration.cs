using Beacon.Core.Adapters.Mail;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Models;
using Beacon.Core.Worker;

namespace Beacon.Core;

public class BeaconConfiguration
{
    public string ConnectionStringName { get; set; } = nameof(Data.BeaconContext);

    /// <summary>
    /// Base URL of the Beacon admin UI (e.g., https://your-domain.com/beacon)
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

    /// <summary>
    /// Approval workflow configuration
    /// </summary>
    public ApprovalWorkflowOptions ApprovalWorkflow { get; set; } = new();

    public void AddBeaconScheduler<T>() where T : class, IBeaconScheduler
    {
        BeaconScheduler = typeof(T);
    }

    public void AddEmailAdapter<T>() where T : class, IEmailAdapter
    {
        EmailAdapter = typeof(T);
    }

    public void AddAuthorizationProvider<T>() where T : class, IBeaconAuthorizationProvider
    {
        Authorization.ProviderType = typeof(T);
    }

    public void AddAuthenticationProvider<T>() where T : class, IBeaconAuthenticationProvider
    {
        Authentication.ProviderType = typeof(T);
    }

    /// <summary>
    /// Enables user management with optional configuration.
    /// This feature allows managing internal users with passwords stored in Beacon,
    /// as well as pre-registering external users for JWT/OAuth authentication.
    /// </summary>
    public void EnableUserManagement(Action<UserManagementOptions>? configure = null)
    {
        UserManagement.Enabled = true;
        configure?.Invoke(UserManagement);
    }

    /// <summary>
    /// Enables approval workflow with optional configuration.
    /// When enabled, SQL changes require admin approval before going live.
    /// </summary>
    public void EnableApprovalWorkflow(Action<ApprovalWorkflowOptions>? configure = null)
    {
        ApprovalWorkflow.Enabled = true;
        configure?.Invoke(ApprovalWorkflow);
    }

    internal Type? BeaconScheduler { get; set; }

    internal Type? EmailAdapter { get; set; }

    internal void ValidateCore()
    {
        if (BeaconScheduler == null)
        {
            throw new BeaconException($"Implementation of IBeaconScheduler is required.");
        }
    }
}
