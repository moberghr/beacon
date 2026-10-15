using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Services;

namespace Beacon.Core.Configuration;

/// <summary>
/// How functional permissions (<see cref="BeaconPermissions"/>) are granted, bound from <c>Beacon:Authorization</c>: the
/// role name each permission is held through (<see cref="Roles"/>), identity-provider app roles mapped onto permissions
/// at single sign-on (<see cref="IdpRoles"/>), and separation of duties (<see cref="SeparationOfDuties"/>).
/// </summary>
public sealed class RoleMappingOptions
{
    public const string SectionName = "Beacon:Authorization";

    /// <summary>The Beacon role that grants each permission (<c>Beacon:Authorization:Roles:{Permission}</c>).</summary>
    public FunctionalRoleNames Roles { get; set; } = new();

    /// <summary>Identity-provider app roles mapped onto permissions at sign-in (<c>Beacon:Authorization:IdpRoles</c>).</summary>
    public IdpRoleOptions IdpRoles { get; set; } = new();

    /// <summary>Conflicting permissions kept apart on role assignment (<c>Beacon:Authorization:SeparationOfDuties</c>).</summary>
    public SeparationOfDutiesOptions SeparationOfDuties { get; set; } = new();

    /// <summary>The role name that grants <paramref name="permission"/>.</summary>
    public string RoleNameFor(string permission)
    {
        var name = permission switch
        {
            BeaconPermissions.Approver => Roles.Approver,
            BeaconPermissions.Auditor => Roles.Auditor,
            BeaconPermissions.SecurityAdmin => Roles.SecurityAdmin,
            BeaconPermissions.UserAdmin => Roles.UserAdmin,
            BeaconPermissions.DataSourceAdmin => Roles.DataSourceAdmin,
            BeaconPermissions.MigrationOperator => Roles.MigrationOperator,
            _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, "Not a functional permission.")
        };

        return string.IsNullOrWhiteSpace(name) ? permission : name.Trim();
    }

    /// <summary>
    /// The permission whose role name <paramref name="roleName"/> is (compared ignoring case, as the role names are
    /// validated); null for any other name. A stored role grants it only when it is a functional role
    /// (<see cref="PermissionOfStoredRole"/>).
    /// </summary>
    public string? PermissionOfRole(string? roleName)
    {
        return BeaconPermissions.All
            .Where(x => string.Equals(RoleNameFor(x), roleName?.Trim(), StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    /// <summary>
    /// The permission a stored role grants: only a functional role does, a system role at level 0
    /// (<see cref="RoleService.RoleLevels.Functional"/>) named as a permission is mapped. A role someone created with the
    /// same name grants nothing. Null for any other role.
    /// </summary>
    public string? PermissionOfStoredRole(string? roleName, bool isSystemRole, int level)
    {
        return isSystemRole && level == RoleService.RoleLevels.Functional
            ? PermissionOfRole(roleName)
            : null;
    }

    /// <summary>
    /// True when <paramref name="roleName"/> names the Admin role or the role of a functional permission (ignoring case):
    /// a role no default must hand out, since everyone it is given to would administer Beacon.
    /// </summary>
    public bool IsPrivilegedRoleName(string? roleName)
    {
        return string.Equals(roleName?.Trim(), RoleService.RoleNames.Admin, StringComparison.OrdinalIgnoreCase)
            || PermissionOfRole(roleName) != null;
    }

    /// <summary>The permissions <paramref name="roleNames"/> grant.</summary>
    public IReadOnlySet<string> PermissionsOfRoles(IEnumerable<string?> roleNames)
    {
        return roleNames
            .Select(PermissionOfRole)
            .OfType<string>()
            .ToHashSet();
    }

    /// <summary>The permission an identity-provider app role maps to; null when it is not mapped, or mapping is off.</summary>
    public string? PermissionOfIdpRole(string? idpRole)
    {
        if (!IdpRoles.Enabled || string.IsNullOrEmpty(idpRole))
        {
            return null;
        }

        return IdpRoles.Map.TryGetValue(idpRole, out var permission)
            ? BeaconPermissions.Find(permission)
            : null;
    }
}

/// <summary>The Beacon role name each functional permission is held through; by default the permission's own name.</summary>
public sealed class FunctionalRoleNames
{
    public string Approver { get; set; } = BeaconPermissions.Approver;

    public string Auditor { get; set; } = BeaconPermissions.Auditor;

    public string SecurityAdmin { get; set; } = BeaconPermissions.SecurityAdmin;

    public string UserAdmin { get; set; } = BeaconPermissions.UserAdmin;

    public string DataSourceAdmin { get; set; } = BeaconPermissions.DataSourceAdmin;

    public string MigrationOperator { get; set; } = BeaconPermissions.MigrationOperator;

    /// <summary>
    /// On a host without Beacon user management, whether the session's role claims (from the host's own sign-in) grant
    /// the permissions whose role they name. Off by default: such a session then holds functional permissions only
    /// through the Admin role claim (which holds them all). With Beacon user management, stored roles decide and this
    /// setting has no effect.
    /// </summary>
    public bool HonourHostRoleClaims { get; set; }
}

/// <summary>
/// Identity-provider app roles that grant functional permissions at single sign-on. Off by default. A mapped permission
/// is added to the session's claims when the user signs in and is never stored: it is honoured for
/// <see cref="ClaimLifetimeMinutes"/> after that sign-in (signing in again renews it). API keys hold no functional
/// permission, mapped or stored.
/// </summary>
public sealed class IdpRoleOptions
{
    public const int DefaultClaimLifetimeMinutes = 60;

    public const int MaxClaimLifetimeMinutes = 1440;

    public bool Enabled { get; set; }

    /// <summary>An app role value the identity provider sends (the <c>roles</c> claim) → a functional permission name.</summary>
    public Dictionary<string, string> Map { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a mapped permission is honoured after the sign-in that mapped it, in minutes (1 to 1440). The session's
    /// cookie lives longer and slides; the mapped permissions do not, so an app role the identity provider withdraws stops
    /// counting at the latest this long after the user last signed in.
    /// </summary>
    public int ClaimLifetimeMinutes { get; set; } = DefaultClaimLifetimeMinutes;
}

/// <summary>
/// Separation of duties. Off by default. On, assigning a user roles that grant both permissions of a pair in
/// <see cref="BeaconPermissions.ConflictingPairs"/> is refused (400), an identity-provider mapping onto both permissions of
/// a pair fails startup, a mapped permission that conflicts with the user's stored roles is not added at sign-in, and a
/// caller who still holds both permissions of a pair (roles assigned before the switch) holds neither of them.
/// </summary>
public sealed class SeparationOfDutiesOptions
{
    public bool Enabled { get; set; }
}

/// <summary>
/// Fails the host at boot on a role mapping that could grant a permission by mistake: an over-long role name, the name of
/// a built-in role (Admin, Editor, Viewer), two permissions mapped to one role (names compared ignoring case),
/// identity-provider mapping switched on with nothing to map or onto something that is not a functional permission, a
/// mapped-claim lifetime outside 1 to 1440 minutes, and, with separation of duties on, app roles mapped onto both
/// permissions of a conflicting pair. A blank role name keeps the default.
/// </summary>
internal sealed class RoleMappingOptionsValidator : IValidateOptions<RoleMappingOptions>
{
    // The roles table's name column length.
    private const int MaxRoleNameLength = 50;

    private static readonly string[] BuiltInRoles =
    [
        RoleService.RoleNames.Admin,
        RoleService.RoleNames.Editor,
        RoleService.RoleNames.Viewer
    ];

    public ValidateOptionsResult Validate(string? name, RoleMappingOptions options)
    {
        var failures = new List<string>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var permission in BeaconPermissions.All)
        {
            var key = $"{RoleMappingOptions.SectionName}:{nameof(RoleMappingOptions.Roles)}:{permission}";
            var roleName = options.RoleNameFor(permission);
            if (roleName.Length > MaxRoleNameLength)
            {
                failures.Add($"{key} must be at most {MaxRoleNameLength} characters.");
            }

            if (BuiltInRoles.Contains(roleName, StringComparer.OrdinalIgnoreCase))
            {
                failures.Add($"{key} cannot be the built-in role '{roleName}'.");
            }

            if (seen.TryGetValue(roleName, out var other))
            {
                failures.Add($"{key} names the same role as {other}; each permission needs its own role.");
                continue;
            }

            seen[roleName] = permission;
        }

        var idpKey = $"{RoleMappingOptions.SectionName}:{nameof(RoleMappingOptions.IdpRoles)}";
        if (options.IdpRoles.Enabled && options.IdpRoles.Map.Count == 0)
        {
            failures.Add($"{idpKey}:Enabled needs at least one entry in {idpKey}:Map.");
        }

        foreach (var (idpRole, permission) in options.IdpRoles.Map)
        {
            if (string.IsNullOrWhiteSpace(idpRole))
            {
                failures.Add($"{idpKey}:Map has an entry without an app role.");
            }

            if (BeaconPermissions.Find(permission) == null)
            {
                failures.Add($"{idpKey}:Map maps an app role onto '{permission}', which is not one of {string.Join(", ", BeaconPermissions.All)}.");
            }
        }

        var lifetime = options.IdpRoles.ClaimLifetimeMinutes;
        if (lifetime < 1 || lifetime > IdpRoleOptions.MaxClaimLifetimeMinutes)
        {
            failures.Add($"{idpKey}:{nameof(IdpRoleOptions.ClaimLifetimeMinutes)} must be between 1 and {IdpRoleOptions.MaxClaimLifetimeMinutes}.");
        }

        if (options.SeparationOfDuties.Enabled)
        {
            var mappedPermissions = options.IdpRoles.Map.Values
                .Select(BeaconPermissions.Find)
                .OfType<string>()
                .ToHashSet();
            var conflict = BeaconPermissions.FirstConflict(mappedPermissions);
            if (conflict != null)
            {
                failures.Add($"{idpKey}:Map maps app roles onto both {conflict.Value.First} and {conflict.Value.Second}, which separation of duties keeps apart.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
