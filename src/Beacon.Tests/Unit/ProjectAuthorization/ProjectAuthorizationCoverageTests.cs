using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Beacon.Api.Endpoints;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.McpEval;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Build-breaking coverage of project authorization: every MediatR request is authorized by id, filtered
/// by scope, or exempt with a reason; every id it carries is checked or exempt with a reason (proven with a sentinel,
/// so a check cannot be dropped silently); every exempt request is gated by the policy it names; and every by-id route
/// either sends an authorized request or is on the explicit owner/admin allow-list below.
/// </summary>
[TestFixture]
public class ProjectAuthorizationCoverageTests
{
    private const int Sentinel = 987_654;

    // An id property: *Id, *Ids, or an id with a suffix (VersionIdA).
    private static readonly Regex IdProperty = new("Ids?(?![a-z])", RegexOptions.Compiled);

    /// <summary>
    /// By-id routes that do not run a project check, each for the reason given. Adding a by-id route means adding a
    /// project check to its request, or a line here.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> OwnerOrAdminRoutes = new Dictionary<string, string>
    {
        ["AdminRevokeApiKey"] = "UserAdmin: key administration",
        ["GetUserOwnedResources"] = "Auditor or UserAdmin: a user's resources across projects, for offboarding",
        ["RevokeApiKey"] = "owner: the caller's own key",
        ["DeleteDataSource"] = "DataSourceAdmin: data-source administration",
        ["UpdateDataSource"] = "DataSourceAdmin: data-source administration (edit, credential rotation)",
        ["SetDataSourceEnabled"] = "DataSourceAdmin: data-source administration (disable, enable)",
        ["SetDataSourceMigrationWrites"] = "Admin: marking a data source as a migration write target",
        ["DeleteGlossaryTerm"] = "Admin: glossary curation",
        ["UpdateGlossaryTerm"] = "Admin: glossary curation",
        ["GetEvalResults"] = "Admin: eval curation",
        ["UpdateGoldenCase"] = "Admin: eval curation",
        ["DeleteMigrationJob"] = "MigrationOperator: data migration",
        ["GetMigrationJob"] = "MigrationOperator: data migration",
        ["RunMigrationJob"] = "MigrationOperator: data migration; the handler checks Run on the source and Write on the destination",
        ["DeleteQueryFolder"] = "query folders are project-less; delete demands Write on every query held",
        ["UpdateQueryFolder"] = "query folders are project-less",
        ["DeleteRecipient"] = "SecurityAdmin: recipients are project-less",
        ["UpdateRecipient"] = "SecurityAdmin: recipients are project-less; a changed destination needs Read on what it is attached to",
        ["GetMcpProjectSettings"] = "Auditor or SecurityAdmin: security configuration",
        ["UpdateMcpProjectSettings"] = "SecurityAdmin: security configuration",
        ["ApproveQueryChange"] = "Approver: approval decision",
        ["RejectQueryChange"] = "Approver: approval decision",
        ["GetChangeApprovalDetail"] = "Approver, or a reader of the change's projects: scope-filtered in the service",
        ["ApproveChange"] = "Approver: approval decision",
        ["RejectChange"] = "Approver: approval decision",
        ["ToggleUserEnabled"] = "UserAdmin: user management",
        ["UpdateUser"] = "UserAdmin: user management",
        ["SetUserRoles"] = "UserAdmin: user management",
        ["SignUserOutEverywhere"] = "UserAdmin: user management",
        ["SetAiActorOwner"] = "UserAdmin: ownership transfer",
        ["SetDataContractOwner"] = "UserAdmin: ownership transfer",
        ["SetSubscriptionOwner"] = "UserAdmin: ownership transfer",
        ["UpdateRepositoryToken"] = "SecurityAdmin: repository credential",
        ["SetProjectAccessMode"] = "UserAdmin: restricting or opening a project",
        ["UnlinkProjectDataSource"] = "Admin: unlinking a data source from a project",
        ["SetQueryMcpTool"] = "SecurityAdmin: publishing a saved query as an MCP tool"
    };

    [Test]
    public void EveryRequest_IsAuthorizedFilteredOrExempt()
    {
        var failures = new List<string>();

        foreach (var request in BeaconApiRoutes.RequestTypes())
        {
            var exemption = BeaconApiRoutes.ExemptionOf(request);
            var isAuthorized = typeof(IProjectAuthorizedRequest).IsAssignableFrom(request);
            var isFiltered = typeof(IProjectFilteredRequest).IsAssignableFrom(request);

            if (exemption != null)
            {
                if (string.IsNullOrWhiteSpace(exemption.Reason))
                {
                    failures.Add($"{request.Name}: exempt without a reason");
                }

                if (isAuthorized || isFiltered)
                {
                    failures.Add($"{request.Name}: exempt and checked at once");
                }

                continue;
            }

            if (!isAuthorized && !isFiltered)
            {
                failures.Add($"{request.Name}: neither IProjectAuthorizedRequest, IProjectFilteredRequest nor [ProjectScopeExempt]");
                continue;
            }

            failures.AddRange(UncoveredIdProperties(request, isAuthorized));
        }

        failures.Should().BeEmpty();
    }

    [Test]
    public void TheFeedbackCommand_IsNotExempt_AndChecksItsSignal()
    {
        BeaconApiRoutes.ExemptionOf(typeof(RecordQueryFeedbackCommand)).Should().BeNull();

        var checks = ((IProjectAuthorizedRequest)new RecordQueryFeedbackCommand(5, Beacon.Core.Data.Enums.McpUserVerdict.Correct)).ProjectChecks.ToList();

        checks.Should().ContainSingle();
        checks[0].Resource.Kind.Should().Be(ProjectResourceKind.QuerySignal);
        checks[0].Action.Should().Be(ProjectAction.Query);
    }

    [Test]
    public void TheAdminPolicyNamedByExemptions_IsTheApisAdminPolicy()
    {
        ProjectScopeExemptAttribute.AdminPolicy.Should().Be(BeaconApiEndpoints.AdminPolicyName);
    }

    [Test]
    public async Task EveryExemptRequest_IsGatedByItsNamedPolicy()
    {
        var routes = await BeaconApiRoutes.ReadAsync();
        var failures = new List<string>();

        foreach (var request in BeaconApiRoutes.RequestTypes())
        {
            var exemption = BeaconApiRoutes.ExemptionOf(request);
            if (exemption?.Policy == null)
            {
                continue;
            }

            var sending = routes
                .Where(x => x.Name == BeaconApiRoutes.StripSuffix(request.Name))
                .ToList();
            if (sending.Count == 0)
            {
                failures.Add($"{request.Name}: names policy {exemption.Policy} but no route sends it");
                continue;
            }

            failures.AddRange(sending
                .Where(x => !x.Policies.Contains(exemption.Policy))
                .Select(x => $"{request.Name}: {x} does not carry {exemption.Policy}"));
        }

        failures.Should().BeEmpty();
    }

    [Test]
    public async Task EveryByIdRoute_ResolvesAProjectCheck()
    {
        var routes = await BeaconApiRoutes.ReadAsync();
        var requests = BeaconApiRoutes.RequestTypes();
        var failures = new List<string>();

        foreach (var route in routes.Where(x => x.IsById).Distinct())
        {
            if (route.Name != null && OwnerOrAdminRoutes.ContainsKey(route.Name))
            {
                continue;
            }

            var request = BeaconApiRoutes.RequestOf(route, requests);
            if (request == null || !typeof(IProjectAuthorizedRequest).IsAssignableFrom(request))
            {
                failures.Add($"{route}: sends {request?.Name ?? "no known request"}, which runs no project check");
                continue;
            }

            if (!((IProjectAuthorizedRequest)RuntimeHelpers.GetUninitializedObject(request)).ProjectChecks.Any() && request.Name != "CreateProjectCommand")
            {
                failures.Add($"{route}: {request.Name} declares no project check");
            }
        }

        failures.Should().BeEmpty();
    }

    [Test]
    public async Task TheOwnerOrAdminAllowList_NamesOnlyRoutesThatExist_AndAreExemptOrAdminGated()
    {
        var routes = await BeaconApiRoutes.ReadAsync();
        var requests = BeaconApiRoutes.RequestTypes();

        foreach (var (name, reason) in OwnerOrAdminRoutes)
        {
            var route = routes.FirstOrDefault(x => x.Name == name);
            route.Should().NotBeNull($"{name} is on the allow-list ({reason})");
            var request = BeaconApiRoutes.RequestOf(route!, requests);
            request.Should().NotBeNull(name);
            BeaconApiRoutes.ExemptionOf(request!).Should().NotBeNull($"{name} skips project checks, so its request says why");
        }
    }

    // Each id property of a checked request must reach a check (a sentinel set on it shows up in a check's resource), or
    // be exempt with a reason. A filtered request's ids only narrow its list and must say so.
    private static IEnumerable<string> UncoveredIdProperties(Type request, bool isAuthorized)
    {
        var properties = request
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => IdProperty.IsMatch(x.Name))
            .ToList();

        foreach (var property in properties)
        {
            var exemption = property.GetCustomAttribute<ProjectScopeExemptAttribute>();
            if (exemption != null)
            {
                if (string.IsNullOrWhiteSpace(exemption.Reason))
                {
                    yield return $"{request.Name}.{property.Name}: exempt without a reason";
                }

                continue;
            }

            if (!isAuthorized)
            {
                yield return $"{request.Name}.{property.Name}: an id on a filtered request must be exempt (filter only)";
                continue;
            }

            if (!ReachesACheck(request, property))
            {
                yield return $"{request.Name}.{property.Name}: not covered by a project check and not exempt";
            }
        }
    }

    private static bool ReachesACheck(Type request, PropertyInfo property)
    {
        var instance = RuntimeHelpers.GetUninitializedObject(request);
        var sentinel = SentinelFor(property.PropertyType);
        if (sentinel == null || property.SetMethod == null)
        {
            return false;
        }

        property.SetValue(instance, sentinel);

        return ((IProjectAuthorizedRequest)instance).ProjectChecks
            .Any(x => x.Resource.Ids.Contains(Sentinel));
    }

    private static object? SentinelFor(Type type)
    {
        if (type == typeof(int) || type == typeof(int?))
        {
            return Sentinel;
        }

        if (type == typeof(int[]))
        {
            return new[] { Sentinel };
        }

        if (type.IsAssignableFrom(typeof(List<int>)))
        {
            return new List<int> { Sentinel };
        }

        return typeof(IEnumerable).IsAssignableFrom(type) ? null : null;
    }
}
