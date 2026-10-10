using Beacon.Core.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>Project access settings bound from <c>Beacon:Authorization</c>.</summary>
public sealed class ProjectAuthorizationOptions
{
    public const string SectionName = "Beacon:Authorization";

    /// <summary>
    /// Restricts every project, whatever its own setting: only members (and Admins) reach a project, and data sources
    /// no project links are Admin-only. Requires project memberships (Beacon user management, or a host
    /// <see cref="IProjectMembershipSource"/>); the host fails at boot otherwise.
    /// </summary>
    public bool RequireProjectMembership { get; set; }
}

/// <summary>
/// Fails the host at boot when every project is restricted but no membership can grant access to one: only Admins
/// would reach any project.
/// </summary>
internal sealed class ProjectAuthorizationOptionsValidator(IServiceScopeFactory scopeFactory)
    : IValidateOptions<ProjectAuthorizationOptions>
{
    public ValidateOptionsResult Validate(string? name, ProjectAuthorizationOptions options)
    {
        if (!options.RequireProjectMembership)
        {
            return ValidateOptionsResult.Success;
        }

        using var scope = scopeFactory.CreateScope();
        var membershipSource = scope.ServiceProvider.GetRequiredService<IProjectMembershipSource>();

        return membershipSource.IsAvailable
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{SectionNameOf(nameof(ProjectAuthorizationOptions.RequireProjectMembership))} requires project memberships: "
                + "enable Beacon user management or register an IProjectMembershipSource.");
    }

    private static string SectionNameOf(string property)
    {
        return $"{ProjectAuthorizationOptions.SectionName}:{property}";
    }
}
