namespace Beacon.Core.Authorization;

/// <summary>
/// The functional permissions that split the Admin role into duties, and the <c>/beacon/api</c> endpoint policies that
/// require them. Each policy admits the Admin role or the role name a permission is mapped to
/// (<see cref="Configuration.RoleMappingOptions"/>; by default the permission's own name). A functional permission grants
/// no project data: the project access evaluator's bypass stays an Admin's. An API key never carries one.
/// </summary>
public static class BeaconPermissions
{
    /// <summary>Approves and rejects query changes, and reads approval requests in every project.</summary>
    public const string Approver = "Approver";

    /// <summary>Reads the MCP audit, the MCP and admin settings (secrets masked) and every user's API keys.</summary>
    public const string Auditor = "Auditor";

    /// <summary>
    /// Changes the MCP and admin settings (not the LLM provider, endpoint or credentials), recipients, MCP tool
    /// publishing and repository access tokens.
    /// </summary>
    public const string SecurityAdmin = "SecurityAdmin";

    /// <summary>Manages users, their roles and API keys, resource owners, and the members and access mode of any project.</summary>
    public const string UserAdmin = "UserAdmin";

    /// <summary>Creates, tests and deletes data sources, links any data source, and changes shared data-source state.</summary>
    public const string DataSourceAdmin = "DataSourceAdmin";

    /// <summary>Runs data migration, between data sources the caller may run and write.</summary>
    public const string MigrationOperator = "MigrationOperator";

    public const string ApproverPolicy = PolicyPrefix + Approver;
    public const string AuditorPolicy = PolicyPrefix + Auditor;
    public const string SecurityAdminPolicy = PolicyPrefix + SecurityAdmin;
    public const string UserAdminPolicy = PolicyPrefix + UserAdmin;
    public const string DataSourceAdminPolicy = PolicyPrefix + DataSourceAdmin;
    public const string MigrationOperatorPolicy = PolicyPrefix + MigrationOperator;

    /// <summary>Reading the security configuration: an Auditor reviews it, a SecurityAdmin changes it.</summary>
    public const string AuditorOrSecurityAdminPolicy = PolicyPrefix + Auditor + "|" + SecurityAdmin;

    /// <summary>Reading every user's API keys: an Auditor reviews them, a UserAdmin revokes them.</summary>
    public const string AuditorOrUserAdminPolicy = PolicyPrefix + Auditor + "|" + UserAdmin;

    // Reserved: a host must not name its own authorization policies with this prefix.
    private const string PolicyPrefix = "BeaconApi.";

    /// <summary>Every functional permission.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Approver,
        Auditor,
        SecurityAdmin,
        UserAdmin,
        DataSourceAdmin,
        MigrationOperator
    ];

    /// <summary>Every functional-permission policy and the permissions it admits (the Admin role admits them all).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Policies { get; } = new Dictionary<string, IReadOnlyList<string>>
    {
        [ApproverPolicy] = [Approver],
        [AuditorPolicy] = [Auditor],
        [SecurityAdminPolicy] = [SecurityAdmin],
        [UserAdminPolicy] = [UserAdmin],
        [DataSourceAdminPolicy] = [DataSourceAdmin],
        [MigrationOperatorPolicy] = [MigrationOperator],
        [AuditorOrSecurityAdminPolicy] = [Auditor, SecurityAdmin],
        [AuditorOrUserAdminPolicy] = [Auditor, UserAdmin]
    };

    /// <summary>
    /// The pairs separation of duties keeps apart (<c>Beacon:Authorization:SeparationOfDuties:Enabled</c>): one user may
    /// not hold both permissions of a pair.
    /// </summary>
    public static IReadOnlyList<(string First, string Second)> ConflictingPairs { get; } =
    [
        (Approver, SecurityAdmin),
        (Auditor, SecurityAdmin),
        (Auditor, UserAdmin),
        (UserAdmin, Approver),
        (UserAdmin, MigrationOperator)
    ];

    /// <summary>The canonical name of <paramref name="value"/> when it names a functional permission (any case); else null.</summary>
    public static string? Find(string? value)
    {
        return All
            .Where(x => string.Equals(x, value?.Trim(), StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    /// <summary>The first conflicting pair <paramref name="permissions"/> holds both of; null when it holds none.</summary>
    public static (string First, string Second)? FirstConflict(IReadOnlyCollection<string> permissions)
    {
        foreach (var pair in ConflictingPairs)
        {
            if (permissions.Contains(pair.First) && permissions.Contains(pair.Second))
            {
                return pair;
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="permissions"/> without either permission of each conflicting pair it holds both of, and those
    /// pairs: under separation of duties a conflicting pair grants neither of its permissions.
    /// </summary>
    public static (IReadOnlySet<string> Kept, IReadOnlyList<(string First, string Second)> Conflicts) WithoutConflicts(IReadOnlyCollection<string> permissions)
    {
        var conflicts = ConflictingPairs
            .Where(x => permissions.Contains(x.First))
            .Where(x => permissions.Contains(x.Second))
            .ToList();
        var kept = permissions
            .Where(x => conflicts.All(y => y.First != x && y.Second != x))
            .ToHashSet();

        return (kept, conflicts);
    }
}
