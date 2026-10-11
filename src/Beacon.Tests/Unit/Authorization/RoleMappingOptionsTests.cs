using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.Users;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Authorization;

/// <summary>
/// How functional permissions are granted: by default through a role named after each permission; through the role names
/// configured under <c>Beacon:Authorization:Roles</c> otherwise; never through a built-in role, and never two permissions
/// through one role. Identity-provider app roles map onto permissions only. The functional roles are seeded at level 0,
/// under their configured names, next to the built-in roles.
/// </summary>
[TestFixture]
public class RoleMappingOptionsTests
{
    [Test]
    public void ByDefault_EachPermission_IsHeldThroughARoleOfItsOwnName()
    {
        var mapping = new RoleMappingOptions();

        BeaconPermissions.All.Should().OnlyContain(x => mapping.RoleNameFor(x) == x && mapping.PermissionOfRole(x) == x);
        mapping.PermissionOfRole(RoleService.RoleNames.Admin).Should().BeNull();
        mapping.PermissionOfRole("approver").Should().Be(BeaconPermissions.Approver, "role names are compared ignoring case, as the validator compares them");
        mapping.PermissionOfStoredRole(BeaconPermissions.Approver, isSystemRole: true, RoleService.RoleLevels.Functional).Should().Be(BeaconPermissions.Approver);
        mapping.PermissionOfStoredRole(BeaconPermissions.Approver, isSystemRole: false, RoleService.RoleLevels.Functional).Should().BeNull("a role someone created with the name grants nothing");
        mapping.PermissionOfStoredRole(BeaconPermissions.Approver, isSystemRole: true, RoleService.RoleLevels.Editor).Should().BeNull("only a level-0 role is functional");
    }

    [Test]
    public void ConfiguredRoleNames_AreBoundFromTheAuthorizationSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:Authorization:Roles:Auditor"] = "Compliance Reviewer",
                ["Beacon:Authorization:IdpRoles:Enabled"] = "true",
                ["Beacon:Authorization:IdpRoles:Map:beacon.security"] = "SecurityAdmin",
                ["Beacon:Authorization:SeparationOfDuties:Enabled"] = "true"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOptions<RoleMappingOptions>().Bind(configuration.GetSection(RoleMappingOptions.SectionName));
        using var provider = services.BuildServiceProvider();

        var mapping = provider.GetRequiredService<IOptions<RoleMappingOptions>>().Value;

        mapping.RoleNameFor(BeaconPermissions.Auditor).Should().Be("Compliance Reviewer");
        mapping.PermissionOfRole("Compliance Reviewer").Should().Be(BeaconPermissions.Auditor);
        mapping.PermissionOfRole(BeaconPermissions.Auditor).Should().BeNull("the permission is held through its configured role only");
        mapping.PermissionOfIdpRole("beacon.security").Should().Be(BeaconPermissions.SecurityAdmin);
        mapping.SeparationOfDuties.Enabled.Should().BeTrue();
    }

    [Test]
    public void IdentityProviderRoles_MapOnlyWhileTheMappingIsOn()
    {
        var mapping = new RoleMappingOptions { IdpRoles = new IdpRoleOptions { Map = { ["beacon.audit"] = "auditor" } } };

        mapping.PermissionOfIdpRole("beacon.audit").Should().BeNull("the mapping is off by default");

        mapping.IdpRoles.Enabled = true;

        mapping.PermissionOfIdpRole("beacon.audit").Should().Be(BeaconPermissions.Auditor);
        mapping.PermissionOfIdpRole("beacon.other").Should().BeNull();
    }

    [Test]
    public void TheDefaults_AreValid()
    {
        new RoleMappingOptionsValidator().Validate(null, new RoleMappingOptions()).Succeeded.Should().BeTrue();
    }

    [TestCase("Admin")]
    [TestCase("editor")]
    [TestCase("Viewer")]
    public void ARoleNameOfABuiltInRole_FailsValidation(string roleName)
    {
        var options = new RoleMappingOptions { Roles = new FunctionalRoleNames { UserAdmin = roleName } };

        var result = new RoleMappingOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue("a built-in role would grant the permission to everyone holding it");
        result.FailureMessage.Should().Contain("Roles:UserAdmin");
    }

    [Test]
    public void TwoPermissionsOnOneRole_FailValidation()
    {
        var options = new RoleMappingOptions { Roles = new FunctionalRoleNames { Approver = "Governance", SecurityAdmin = "governance" } };

        var result = new RoleMappingOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("own role");
    }

    [Test]
    public void AnOverLongRoleName_FailsValidation()
    {
        var options = new RoleMappingOptions { Roles = new FunctionalRoleNames { Auditor = new string('a', 51) } };

        new RoleMappingOptionsValidator().Validate(null, options).Failed.Should().BeTrue();
    }

    [Test]
    public void IdentityProviderMapping_OnWithoutEntries_OrOntoAnythingButAPermission_FailsValidation()
    {
        var empty = new RoleMappingOptions { IdpRoles = new IdpRoleOptions { Enabled = true } };
        var ontoAdmin = new RoleMappingOptions { IdpRoles = new IdpRoleOptions { Enabled = true, Map = { ["beacon.admin"] = "Admin" } } };
        var valid = new RoleMappingOptions { IdpRoles = new IdpRoleOptions { Enabled = true, Map = { ["beacon.ops"] = "MigrationOperator" } } };

        new RoleMappingOptionsValidator().Validate(null, empty).Failed.Should().BeTrue();
        new RoleMappingOptionsValidator().Validate(null, ontoAdmin).FailureMessage.Should().Contain("not one of");
        new RoleMappingOptionsValidator().Validate(null, valid).Succeeded.Should().BeTrue();
    }

    [TestCase(0, false)]
    [TestCase(1, true)]
    [TestCase(60, true)]
    [TestCase(1440, true)]
    [TestCase(1441, false)]
    public void TheMappedClaimLifetime_IsValidated(int minutes, bool valid)
    {
        var options = new RoleMappingOptions { IdpRoles = new IdpRoleOptions { ClaimLifetimeMinutes = minutes } };

        var result = new RoleMappingOptionsValidator().Validate(null, options);

        result.Succeeded.Should().Be(valid);
        if (!valid)
        {
            result.FailureMessage.Should().Contain("ClaimLifetimeMinutes");
        }
    }

    [Test]
    public void IdentityProviderMappingOntoBothPermissionsOfAConflictingPair_FailsValidation_OnlyUnderSeparationOfDuties()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["beacon.audit"] = BeaconPermissions.Auditor,
            ["beacon.users"] = BeaconPermissions.UserAdmin
        };
        var separated = new RoleMappingOptions
        {
            IdpRoles = new IdpRoleOptions { Enabled = true, Map = map },
            SeparationOfDuties = new SeparationOfDutiesOptions { Enabled = true }
        };
        var notSeparated = new RoleMappingOptions { IdpRoles = new IdpRoleOptions { Enabled = true, Map = map } };

        var refused = new RoleMappingOptionsValidator().Validate(null, separated);

        refused.Failed.Should().BeTrue();
        refused.FailureMessage.Should().Contain("Auditor").And.Contain("UserAdmin").And.Contain("separation of duties");
        new RoleMappingOptionsValidator().Validate(null, notSeparated).Succeeded.Should().BeTrue();
    }

    [Test]
    public void AnAdminOrFunctionalRoleName_IsPrivileged_IgnoringCase()
    {
        var mapping = new RoleMappingOptions { Roles = new FunctionalRoleNames { Auditor = "Compliance Reviewer" } };

        mapping.IsPrivilegedRoleName("admin").Should().BeTrue();
        mapping.IsPrivilegedRoleName(" compliance reviewer ").Should().BeTrue();
        mapping.IsPrivilegedRoleName(BeaconPermissions.UserAdmin).Should().BeTrue();
        mapping.IsPrivilegedRoleName("Viewer").Should().BeFalse();
        mapping.IsPrivilegedRoleName(null).Should().BeFalse();
    }

    [Test]
    public async Task Seeding_ComparesNamesIgnoringCase_AndNeverConvertsARoleThatOnlyHasAFunctionalRolesName()
    {
        var roles = new List<BeaconRole>
        {
            new() { Id = 1, Name = "admin", Level = RoleService.RoleLevels.Admin },
            new() { Id = 2, Name = RoleService.RoleNames.Editor, Level = RoleService.RoleLevels.Editor },
            new() { Id = 3, Name = RoleService.RoleNames.Viewer, Level = RoleService.RoleLevels.Viewer },
            new() { Id = 4, Name = "approver", Level = RoleService.RoleLevels.Functional },
            new() { Id = 9, Name = "Auditor", Level = RoleService.RoleLevels.Editor, IsSystemRole = false }
        };
        var saved = new List<object>();
        var logs = new LogRecorder();

        await new RoleService(Factory(roles, saved), Options.Create(new RoleMappingOptions()), logs.For<RoleService>()).SeedSystemRolesAsync();

        saved.OfType<BeaconRole>().Select(x => x.Name).Should().BeEquivalentTo(
            [BeaconPermissions.SecurityAdmin, BeaconPermissions.UserAdmin, BeaconPermissions.DataSourceAdmin, BeaconPermissions.MigrationOperator],
            "Admin and Approver exist under another case, and the custom Auditor role keeps its name");
        roles.Should().ContainSingle(x => x.Id == 9).Which.Level.Should().Be(RoleService.RoleLevels.Editor, "a custom role is never converted");
        var warning = logs.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("Role 9").And.Contain("Auditor").And.Contain("grants nothing");
    }

    [Test]
    public async Task Seeding_ARoleLostToAConcurrentSeeding_KeepsTheOthers_AndNotesARoleStillMissing()
    {
        var roles = new List<BeaconRole>
        {
            new() { Id = 1, Name = RoleService.RoleNames.Admin, Level = RoleService.RoleLevels.Admin },
            new() { Id = 2, Name = RoleService.RoleNames.Editor, Level = RoleService.RoleLevels.Editor },
            new() { Id = 3, Name = RoleService.RoleNames.Viewer, Level = RoleService.RoleLevels.Viewer }
        };
        var saved = new List<object>();
        var logs = new LogRecorder();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new UniqueViolationOnSaveContext(roles, saved, BeaconPermissions.UserAdmin));

        await new RoleService(factory.Object, Options.Create(new RoleMappingOptions()), logs.For<RoleService>()).SeedSystemRolesAsync();

        saved.OfType<BeaconRole>().Select(x => x.Name).Should().BeEquivalentTo(
            [BeaconPermissions.Approver, BeaconPermissions.Auditor, BeaconPermissions.SecurityAdmin, BeaconPermissions.DataSourceAdmin, BeaconPermissions.MigrationOperator]);
        logs.Entries.Should().ContainSingle().Which.Message.Should().Be("The UserAdmin role could not be added and is still missing");
    }

    [Test]
    public async Task TheRolesList_NamesThePermissionOfFunctionalRolesOnly()
    {
        var roleService = new Mock<IRoleService>();
        roleService
            .Setup(x => x.GetRolesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new BeaconRoleData { Id = 1, Name = RoleService.RoleNames.Admin, Level = RoleService.RoleLevels.Admin, IsSystemRole = true },
                new BeaconRoleData { Id = 4, Name = BeaconPermissions.Approver, Level = RoleService.RoleLevels.Functional, IsSystemRole = true },
                new BeaconRoleData { Id = 9, Name = BeaconPermissions.Auditor, Level = RoleService.RoleLevels.Editor, IsSystemRole = false }
            ]);

        var result = await new GetRolesHandler(roleService.Object, Options.Create(new RoleMappingOptions())).Handle(new GetRolesQuery(), CancellationToken.None);

        result.Entries.Select(x => (x.Id, x.Permission)).Should().Equal((1, null), (4, BeaconPermissions.Approver), (9, null));
    }

    [Test]
    public async Task Seeding_AddsTheFunctionalRolesAtLevelZero_UnderTheirConfiguredNames()
    {
        var roles = new List<BeaconRole>
        {
            new() { Id = 1, Name = RoleService.RoleNames.Admin, Level = RoleService.RoleLevels.Admin },
            new() { Id = 2, Name = RoleService.RoleNames.Editor, Level = RoleService.RoleLevels.Editor },
            new() { Id = 3, Name = RoleService.RoleNames.Viewer, Level = RoleService.RoleLevels.Viewer },
            new() { Id = 4, Name = BeaconPermissions.Approver, Level = RoleService.RoleLevels.Functional }
        };
        var saved = new List<object>();
        var mapping = new RoleMappingOptions { Roles = new FunctionalRoleNames { Auditor = "Compliance Reviewer" } };

        await new RoleService(Factory(roles, saved), Options.Create(mapping), NullLogger<RoleService>.Instance).SeedSystemRolesAsync();

        var added = saved.OfType<BeaconRole>().ToList();
        added.Select(x => x.Name).Should().BeEquivalentTo(
            ["Compliance Reviewer", BeaconPermissions.SecurityAdmin, BeaconPermissions.UserAdmin, BeaconPermissions.DataSourceAdmin, BeaconPermissions.MigrationOperator],
            "the Approver role exists already and the built-in roles are not seeded twice");
        added.Should().OnlyContain(x => x.Level == RoleService.RoleLevels.Functional && x.IsSystemRole);
        added.Should().OnlyContain(x => x.Description!.Contains("Grants no access to project data"));
    }

    private static IDbContextFactory<BeaconContext> Factory(List<BeaconRole> roles, List<object> saved)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var set = RecordingBeaconContext.MemorySet(roles, saved);
                Mock.Get(set)
                    .Setup(x => x.AddRange(It.IsAny<IEnumerable<BeaconRole>>()))
                    .Callback<IEnumerable<BeaconRole>>(saved.AddRange);

                return new RecordingBeaconContext(new Dictionary<Type, object> { [typeof(BeaconRole)] = set }, saved);
            });

        return factory.Object;
    }

    // Saving the role named failOn throws a unique violation, as a concurrent seeding's insert would; other roles save.
    private sealed class UniqueViolationOnSaveContext(List<BeaconRole> roles, List<object> saved, string failOn) : BeaconContext(Options, "beacon")
    {
        private static readonly DbContextOptions<UniqueViolationOnSaveContext> Options =
            new DbContextOptionsBuilder<UniqueViolationOnSaveContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly List<BeaconRole> _pending = [];

        public override DbSet<TEntity> Set<TEntity>()
        {
            if (typeof(TEntity) != typeof(BeaconRole))
            {
                return base.Set<TEntity>();
            }

            var set = RecordingBeaconContext.MemorySet(roles, []);
            Mock.Get(set)
                .Setup(x => x.Add(It.IsAny<BeaconRole>()))
                .Callback<BeaconRole>(_pending.Add);

            return (DbSet<TEntity>)(object)set;
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            if (_pending.Any(x => x.Name == failOn))
            {
                throw new DbUpdateException("duplicate", new Npgsql.PostgresException("duplicate key", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.UniqueViolation));
            }

            saved.AddRange(_pending);

            return Task.FromResult(_pending.Count);
        }
    }
}
