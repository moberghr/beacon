using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;

namespace Beacon.Core.Configuration;

/// <summary>
/// Where notification recipients may point, bound from <c>Beacon:Notifications</c>. An absent section keeps the
/// built-in policy: Slack, Teams and Jira destinations must be https URLs on the vendor's own hosts
/// (<see cref="NotificationDestinationPolicy"/>), generic webhooks may target any public https host, email may go to
/// any domain, every notification type is enabled, no private network is reachable, and calls connect directly.
/// </summary>
public sealed class NotificationChannelOptions
{
    public const string SectionName = "Beacon:Notifications";

    /// <summary>
    /// Extra hosts per notification type, added to the built-in Slack, Teams and Jira hosts. For Webhook and Email a
    /// non-empty list is the whole allow-list (Email entries are domains); empty means any public host or domain.
    /// An entry is an exact host (<c>hooks.example.com</c>) or a wildcard (<c>*.example.com</c>, any subdomain, not
    /// the domain itself).
    /// </summary>
    public NotificationAllowedHosts AllowedHosts { get; set; } = new();

    /// <summary>Notification types this host does not deliver: recipients of these types cannot be saved or sent to.</summary>
    public List<NotificationType> DisabledTypes { get; set; } = [];

    /// <summary>
    /// Private, loopback or link-local destinations calls of one notification type may still connect to, for example an
    /// on-premises Jira (<c>AllowedPrivateNetworks:Jira</c>); an entry for one type never opens another. Each entry is an
    /// IP address, a CIDR range (<c>10.20.0.0/16</c>) or a host name (exact or <c>*.example.com</c>). A host-name entry
    /// never reaches link-local or metadata addresses. Empty by default: only public addresses are reachable.
    /// </summary>
    public NotificationPrivateNetworks AllowedPrivateNetworks { get; set; } = new();

    /// <summary>
    /// Webhook header names allowed on top of the built-in <c>Authorization</c>, <c>Api-Key</c>, <c>X-Api-Key</c> and
    /// <c>Ocp-Apim-Subscription-Key</c>. Headers that control the connection, routing, cookies or cloud metadata
    /// (<c>Host</c>, <c>Content-Length</c>, <c>X-Forwarded-For</c>, <c>Metadata-Flavor</c>, …) cannot be added.
    /// </summary>
    public List<string> AllowedHeaders { get; set; } = [];

    /// <summary>
    /// Extra NAT64 prefixes (IPv6 <c>/96</c> ranges) used on this network, besides the well-known <c>64:ff9b::/96</c>.
    /// An address in one is judged by the IPv4 address it carries.
    /// </summary>
    public List<string> Nat64Prefixes { get; set; } = [];

    /// <summary>
    /// Send notification calls through the system (default) HTTP proxy, for hosts whose egress must go through one.
    /// Off by default: Beacon then connects directly and vets the address it connects to. When on, the connect-time
    /// check sees only the proxy, so Beacon resolves each destination itself before sending and refuses it unless every
    /// resolved address is public or allowed; the proxy resolves the name again, so it must enforce its own egress
    /// policy (a DNS answer that changes in between is the proxy's to catch). A destination Beacon cannot resolve is
    /// refused. Generic webhooks then need an explicit <c>AllowedHosts:Webhook</c> list (or Webhook disabled).
    /// </summary>
    public bool UseSystemProxy { get; set; }

    /// <summary>
    /// Refuse to send to a recipient whose destination or headers are still stored unencrypted (saved by an earlier
    /// version). Off by default so upgraded hosts keep delivering; turn it on once
    /// <see cref="IRecipientSecretEncryptionService"/> has encrypted every stored secret.
    /// </summary>
    public bool RequireEncryptedSecrets { get; set; }
}

public sealed class NotificationAllowedHosts
{
    public List<string> Slack { get; set; } = [];

    public List<string> Teams { get; set; } = [];

    public List<string> Jira { get; set; } = [];

    public List<string> Webhook { get; set; } = [];

    /// <summary>Allowed email domains. Empty means any domain.</summary>
    public List<string> Email { get; set; } = [];

    public IReadOnlyList<string> For(NotificationType type)
    {
        return type switch
        {
            NotificationType.Slack => Slack,
            NotificationType.Teams => Teams,
            NotificationType.Jira => Jira,
            NotificationType.Webhook => Webhook,
            NotificationType.Email => Email,
            _ => [],
        };
    }
}

/// <summary>Private-network allowances per notification type that makes HTTP calls.</summary>
public sealed class NotificationPrivateNetworks
{
    public List<string> Webhook { get; set; } = [];

    public List<string> Slack { get; set; } = [];

    public List<string> Teams { get; set; } = [];

    public List<string> Jira { get; set; } = [];

    public IReadOnlyList<string> For(NotificationType type)
    {
        return type switch
        {
            NotificationType.Webhook => Webhook,
            NotificationType.Slack => Slack,
            NotificationType.Teams => Teams,
            NotificationType.Jira => Jira,
            _ => [],
        };
    }
}

/// <summary>
/// Fails the host at boot on a malformed or unsafe entry, naming each one, and logs the effective notification policy
/// once it is valid (no secrets: types, host patterns and counts only).
/// </summary>
internal sealed class NotificationChannelOptionsValidator(ILogger<NotificationChannelOptionsValidator> logger)
    : IValidateOptions<NotificationChannelOptions>
{
    private const string Section = NotificationChannelOptions.SectionName;

    public ValidateOptionsResult Validate(string? name, NotificationChannelOptions options)
    {
        var failures = new List<string>();

        foreach (var type in Enum.GetValues<NotificationType>())
        {
            foreach (var pattern in options.AllowedHosts.For(type))
            {
                if (!HostPattern.IsValid(pattern))
                {
                    failures.Add($"{Section}:AllowedHosts:{type} has an invalid entry '{pattern}'. Use a host name or '*.domain' (at least two labels after '*.').");
                }
            }
        }

        foreach (var type in OutboundAddressPolicy.HttpTypes)
        {
            foreach (var entry in options.AllowedPrivateNetworks.For(type))
            {
                if (!OutboundAddressPolicy.IsValidAllowListEntry(entry))
                {
                    failures.Add($"{Section}:AllowedPrivateNetworks:{type} has an invalid entry '{entry}'. Use an IP address, a CIDR range with no host bits set, or a host name.");
                }
            }
        }

        foreach (var header in options.AllowedHeaders)
        {
            if (!NotificationDestinationPolicy.IsConfigurableHeaderName(header))
            {
                failures.Add($"{Section}:AllowedHeaders has an invalid or reserved header name '{header}'.");
            }
        }

        foreach (var prefix in options.Nat64Prefixes)
        {
            if (!OutboundAddressPolicy.IsValidNat64Prefix(prefix))
            {
                failures.Add($"{Section}:Nat64Prefixes has an invalid entry '{prefix}'. Use an IPv6 /96 range with no host bits set.");
            }
        }

        if (options.UseSystemProxy
            && options.AllowedHosts.Webhook.Count == 0
            && !options.DisabledTypes.Contains(NotificationType.Webhook))
        {
            failures.Add($"{Section}:UseSystemProxy needs {Section}:AllowedHosts:Webhook (or Webhook in {Section}:DisabledTypes): through a proxy, generic webhooks must be limited to listed hosts.");
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        LogEffectivePolicy(options);
        return ValidateOptionsResult.Success;
    }

    private void LogEffectivePolicy(NotificationChannelOptions options)
    {
        var hosts = Enum.GetValues<NotificationType>()
            .Select(x => $"{x}: {Describe(x, options.AllowedHosts.For(x))}");
        var privateNetworks = OutboundAddressPolicy.HttpTypes
            .Select(x => $"{x}: {options.AllowedPrivateNetworks.For(x).Count}");

        logger.LogInformation(
            "Notification policy: disabled types [{DisabledTypes}]; allowed hosts {AllowedHosts}; private-network entries {PrivateNetworks}; extra headers [{AllowedHeaders}]; extra NAT64 prefixes {Nat64PrefixCount}; system proxy {UseSystemProxy}; encrypted secrets required {RequireEncryptedSecrets}",
            string.Join(", ", options.DisabledTypes),
            string.Join("; ", hosts),
            string.Join(", ", privateNetworks),
            string.Join(", ", options.AllowedHeaders),
            options.Nat64Prefixes.Count,
            options.UseSystemProxy,
            options.RequireEncryptedSecrets);
    }

    private static string Describe(NotificationType type, IReadOnlyList<string> configured)
    {
        var builtIn = NotificationDestinationPolicy.BuiltInHosts.TryGetValue(type, out var hosts) ? hosts : [];
        if (builtIn.Length == 0 && configured.Count == 0)
        {
            return "any";
        }

        return string.Join(", ", builtIn.Concat(configured));
    }
}
