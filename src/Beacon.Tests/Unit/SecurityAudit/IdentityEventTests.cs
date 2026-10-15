using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.Setup;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The identity and key events, one per action, each staged in the action's own save: who acted (the caller's
/// <c>Users.Id</c>, or the user signing in), on which user or key, with ids, counts and flags only — never the user name,
/// e-mail address, external id, password, setup token or key material in play.
/// </summary>
[TestFixture]
public class IdentityEventTests
{
    private const int AdminId = 3;
    private const int BenId = 7;
    private const string BenName = "ben.typed-name";
    private const string BenEmail = "ben@corp.example";
    private const string BenExternalId = "ext-ben-0001";
    private const string Password = "Aa1!correct-horse";
    private const int EditorRole = 2;
    private const int ViewerRole = 3;

    private List<BeaconUser> _users = null!;
    private List<BeaconRole> _roles = null!;
    private List<BeaconUserRole> _userRoles = null!;
    private List<ApiKeyCredential> _keys = null!;
    private List<object> _saved = null!;
    private RecordingSecurityAudit _audit = null!;

    [SetUp]
    public void SetUp()
    {
        _users =
        [
            new BeaconUser { Id = 1, ExternalId = "ext-root", UserName = "root", IsSuperAdmin = true, IsEnabled = true, IsInternalUser = true },
            new BeaconUser { Id = BenId, ExternalId = BenExternalId, UserName = BenName, Email = BenEmail, DisplayName = "Ben B", IsEnabled = true, IsInternalUser = true, PasswordHash = "hash", PasswordSalt = "salt" }
        ];
        _roles =
        [
            new BeaconRole { Id = EditorRole, Name = "Editor", Level = 2, IsSystemRole = true },
            new BeaconRole { Id = ViewerRole, Name = "Viewer", Level = 1, IsSystemRole = true }
        ];
        _userRoles = [new BeaconUserRole { UserId = BenId, RoleId = ViewerRole }];
        _keys = [new ApiKeyCredential { Id = 21, UserId = BenId, Name = "ci", KeyHash = "h", KeyPrefix = "sk-sem_abc" }];
        _saved = [];
        _audit = new RecordingSecurityAudit(Caller(AdminId));
    }

    [Test]
    public async Task CreatingAnInternalUser_RecordsUserCreated_WithItsRoles()
    {
        (await Users().CreateInternalUserAsync(new CreateInternalUserRequest
        {
            UserName = "new.person",
            Email = "new.person@corp.example",
            Password = Password,
            RoleIds = [EditorRole]
        })).Success.Should().BeTrue();

        var created = _audit.Single("new.person", Password);
        created.Should().BeEquivalentTo(new { Action = SecurityAuditActions.UserCreated, ActorUserId = (int?)AdminId, ResourceType = SecurityAuditResources.User }, x => x.ExcludingMissingMembers());
        created.DetailsJson.Should().Be("{\"internal\":true,\"roleIds\":[2]}");
        _audit.Ways.Should().Equal("saved");
    }

    [Test]
    public async Task PreRegisteringAnExternalUser_RecordsUserCreated_NeverItsExternalId()
    {
        (await Users().CreateExternalUserAsync(new CreateExternalUserRequest
        {
            ExternalId = "oid-9f1c-subject",
            UserName = "ext.person",
            RoleIds = [ViewerRole]
        })).Success.Should().BeTrue();

        _audit.Single("oid-9f1c-subject", "ext.person").DetailsJson.Should().Be("{\"internal\":false,\"roleIds\":[3]}");
    }

    [Test]
    public async Task ProvisioningAtAFirstSignIn_IsRecordedAsTheSystems_WithTheDefaultRole()
    {
        await Users(roleService: SeedingRoles()).GetOrCreateExternalUserAsync("oid-new-subject", "https://login.example/tenant", "sso.person", "sso@corp.example", null, "Viewer");

        var provisioned = _audit.Single("oid-new-subject", "sso.person", "login.example");
        provisioned.Action.Should().Be(SecurityAuditActions.UserProvisioned);
        provisioned.ActorKind.Should().Be(SecurityAuditActorKinds.System, "Beacon provisions the user; the request has no caller yet");
        provisioned.DetailsJson.Should().Be("{\"roleIds\":[3]}");
    }

    [Test]
    public async Task UpdatingAUser_RecordsWhichFieldsChanged_NotTheirValues()
    {
        (await Users().UpdateUserAsync(new UpdateUserRequest { UserId = BenId, UserName = "ben.renamed", Email = BenEmail, DisplayName = "Ben B", IsEnabled = false })).Success.Should().BeTrue();

        var updated = _audit.Single(BenName, "ben.renamed", BenEmail);
        updated.Should().BeEquivalentTo(new { Action = SecurityAuditActions.UserUpdated, ActorUserId = (int?)AdminId, ResourceId = "7" }, x => x.ExcludingMissingMembers());
        updated.DetailsJson.Should().Be(
            "{\"userNameChanged\":true,\"emailChanged\":false,\"displayNameChanged\":false,\"enabledBefore\":true,\"enabledAfter\":false," +
            "\"sessionsSignedOut\":true,\"apiKeysRevoked\":1}");
        _audit.Ways.Should().Equal("staged");
    }

    [Test]
    public async Task SavingAUserUnchanged_RecordsNothing()
    {
        (await Users().UpdateUserAsync(new UpdateUserRequest { UserId = BenId, UserName = BenName, Email = BenEmail, DisplayName = "Ben B", IsEnabled = true })).Success.Should().BeTrue();

        _audit.Events.Should().BeEmpty();
    }

    [TestCase(true, SecurityAuditActions.UserDisabled)]
    [TestCase(false, SecurityAuditActions.UserEnabled)]
    public async Task TogglingAUser_RecordsEnabledOrDisabled_WithTheKeysItRevoked(bool enabled, string action)
    {
        _users.Single(x => x.Id == BenId).IsEnabled = enabled;

        (await Users().ToggleUserEnabledAsync(BenId)).Success.Should().BeTrue();

        var toggled = _audit.Single(BenName, BenEmail);
        toggled.Should().BeEquivalentTo(new { Action = action, ActorUserId = (int?)AdminId, ResourceId = "7", DetailsJson = "{\"apiKeysRevoked\":1}" }, x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task ArchivingAUser_RecordsUserArchived()
    {
        (await Users().DeleteUserAsync(BenId)).Success.Should().BeTrue();

        _audit.Single(BenName).Should().BeEquivalentTo(new { Action = SecurityAuditActions.UserArchived, ResourceId = "7", DetailsJson = "{\"apiKeysRevoked\":1}" }, x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task AssigningAndRemovingARole_RecordsRolesChanged_ByRoleId()
    {
        (await Users().AssignRoleAsync(BenId, EditorRole, "ext-admin")).Success.Should().BeTrue();
        (await Users().RemoveRoleAsync(BenId, ViewerRole)).Success.Should().BeTrue();

        _audit.Events.Select(x => (x.Action, x.ResourceId, x.DetailsJson)).Should().Equal(
            (SecurityAuditActions.RolesChanged, "7", "{\"addedRoleIds\":[2],\"removedRoleIds\":[]}"),
            (SecurityAuditActions.RolesChanged, "7", "{\"addedRoleIds\":[],\"removedRoleIds\":[3]}"));
        foreach (var auditEvent in _audit.Events)
        {
            SecurityAuditTestKit.AssertNoPii(auditEvent, BenName, "ext-admin");
        }
    }

    [Test]
    public async Task AnAdministratorSettingAPassword_RecordsPasswordReset_NeverThePassword()
    {
        (await Users().ResetPasswordAsync(BenId, Password)).Success.Should().BeTrue();

        _audit.Single(Password, BenName).Should().BeEquivalentTo(new { Action = SecurityAuditActions.PasswordReset, ActorUserId = (int?)AdminId, ResourceId = "7" }, x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task AUserChangingTheirPassword_RecordsPasswordChanged()
    {
        _audit = new RecordingSecurityAudit(Caller(BenId));

        (await Users(passwordMatches: true).ChangePasswordAsync(BenId, "old-password", Password)).Success.Should().BeTrue();

        _audit.Single(Password, "old-password").Should().BeEquivalentTo(new { Action = SecurityAuditActions.PasswordChanged, ActorUserId = (int?)BenId, ResourceId = "7" }, x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task ChoosingTheRequiredNewPasswordWhileSigningIn_IsRecordedAsTheUsers()
    {
        _audit = new RecordingSecurityAudit(BeaconActor.Nobody);
        _users.Single(x => x.Id == BenId).MustChangePassword = true;

        var result = await Users(passwordMatches: true).CompleteRequiredPasswordChangeAsync(BenName, "set-by-admin-1A!", Password);

        result.Success.Should().BeTrue();
        _audit.Single(Password, BenName, "set-by-admin-1A!").Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.PasswordChanged, ActorKind = SecurityAuditActorKinds.User, ActorUserId = (int?)BenId, DetailsJson = "{\"required\":true}" },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task AFailedPasswordCheck_NamesTheResolvedUserForTheAudit_AndALocalLoginRefusal()
    {
        var wrong = await Users(passwordMatches: false).AuthenticateInternalUserAsync(BenName, "wrong");
        var unknown = await Users(passwordMatches: false).AuthenticateInternalUserAsync("nobody", "wrong");
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.LocalLogin.Enabled = false;
        var refused = await Users(passwordMatches: true, configuration: configuration).AuthenticateInternalUserAsync(BenName, Password);

        wrong.Should().BeEquivalentTo(new { Success = false, BeaconUserId = (int?)BenId, LocalLoginRefused = false }, x => x.ExcludingMissingMembers());
        unknown.Should().BeEquivalentTo(new { Success = false, BeaconUserId = (int?)null, LocalLoginRefused = false }, x => x.ExcludingMissingMembers());
        refused.Should().BeEquivalentTo(new { Success = false, BeaconUserId = (int?)BenId, LocalLoginRefused = true }, x => x.ExcludingMissingMembers());
        _audit.Events.Should().BeEmpty("the sign-in endpoint records the attempt");
    }

    [Test]
    public async Task FirstRunSetup_RecordsTheSuperAdminsCreation_AsAnonymous()
    {
        _users.Clear();
        var capture = new SqlCapture();

        await Users(offline: capture, roleService: SeedingRoles(), roles: [new BeaconRole { Id = 1, Name = RoleService.RoleNames.Admin, Level = 3, IsSystemRole = true }])
            .CreateSuperAdminAsync(new CreateSuperAdminRequest { UserName = "first.admin", Email = "first@corp.example", Password = Password, ConfirmPassword = Password });

        var created = _audit.Single("first.admin", Password);
        created.Should().BeEquivalentTo(new { Action = SecurityAuditActions.SuperAdminCreated, ActorKind = SecurityAuditActorKinds.Anonymous, DetailsJson = "{\"roleIds\":[1]}" }, x => x.ExcludingMissingMembers());
        capture.Commits.Should().Be(1, "the user and its event commit in the setup's transaction");
    }

    [Test]
    public async Task ARefusedSetupToken_IsRecorded_NeverTheTokenOrTheName()
    {
        var users = new Mock<IUserManagementService>();
        users.Setup(x => x.IsFirstRunAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.SetupToken = "the-configured-setup-token-0001";
        var token = new FirstRunSetupToken(configuration, NullLogger<FirstRunSetupToken>.Instance, TextWriter.Null);
        _audit = new RecordingSecurityAudit(BeaconActor.Nobody);

        var result = await new CreateSuperAdminHandler(users.Object, token, _audit, NullLogger<CreateSuperAdminHandler>.Instance).Handle(
            new CreateSuperAdminCommand(new CreateSuperAdminRequest { UserName = "takeover.name", Password = Password, ConfirmPassword = Password, SetupToken = "guessed-token-value-0001" }),
            CancellationToken.None);

        result.TokenRejected.Should().BeTrue();
        _audit.Single("guessed-token-value-0001", "the-configured-setup-token-0001", "takeover.name").Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.SetupTokenRefused, Outcome = SecurityAuditOutcomes.Refused, ActorKind = SecurityAuditActorKinds.Anonymous, DetailsJson = "{\"tokenPresent\":true}" },
            x => x.ExcludingMissingMembers());
        _audit.Ways.Should().Equal("recorded");
    }

    [Test]
    public async Task MintingAKey_RecordsItsScopesProjectsAndExpiry_NeverItsMaterialOrName()
    {
        var (service, _) = ApiKeys(Caller(BenId));

        var (credential, plainText) = await service.GenerateApiKeyAsync(BenId, "ben's laptop key", ["Read", "Execute"], [4], ct: CancellationToken.None);

        var minted = _audit.Single(plainText, plainText[..16], "ben's laptop key", credential.KeyHash);
        minted.Should().BeEquivalentTo(new { Action = SecurityAuditActions.ApiKeyCreated, ActorUserId = (int?)BenId, ResourceType = SecurityAuditResources.ApiKey }, x => x.ExcludingMissingMembers());
        minted.DetailsJson.Should().StartWith("{\"ownerUserId\":7,\"readScope\":true,\"executeScope\":true,\"allowedProjectIds\":[4],\"expiresAt\":\"2027-01-12T09:00:00Z\"}");
    }

    [TestCase(BenId, SecurityAuditActions.ApiKeyRevoked)]
    [TestCase(AdminId, SecurityAuditActions.ApiKeyRevokedByAdministrator)]
    public async Task RevokingAKey_IsTheOwnersRevocation_OrAnAdministrators(int callerId, string action)
    {
        var (service, _) = ApiKeys(Caller(callerId));

        await service.RevokeApiKeyAsync(21);

        _audit.Single().Should().BeEquivalentTo(
            new { Action = action, ActorUserId = (int?)callerId, ResourceId = "21", DetailsJson = "{\"ownerUserId\":7}" },
            x => x.ExcludingMissingMembers());
        _audit.Ways.Should().Equal("staged");
    }

    [Test]
    public async Task RevokingARevokedKey_RecordsNothing()
    {
        _keys[0].IsRevoked = true;
        var (service, _) = ApiKeys(Caller(BenId));

        await service.RevokeApiKeyAsync(21);

        _audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task SigningAUserOutEverywhere_RecordsIt_WithTheNewStampInOneUnit_AndNothingForAnUnknownUser()
    {
        var capture = new SqlCapture().ThenRowsAffected(1).ThenRowsAffected(0);

        var renewed = await new UserSessionStore(capture.Factory(), _audit).RenewStampAsync(BenId, CancellationToken.None);
        var missing = await new UserSessionStore(capture.Factory(), _audit).RenewStampAsync(404, CancellationToken.None);

        renewed.Should().BeTrue();
        missing.Should().BeFalse();
        _audit.Single().Should().BeEquivalentTo(new { Action = SecurityAuditActions.SignedOutEverywhere, ActorUserId = (int?)AdminId, ResourceId = "7" }, x => x.ExcludingMissingMembers());
    }

    private static BeaconActor Caller(int userId)
    {
        return new BeaconActor($"ext-caller-{userId}", false) { Kind = BeaconPrincipalKind.User, BeaconUserId = userId, IsActiveUser = true };
    }

    private static Mock<IRoleService> SeedingRoles()
    {
        var roles = new Mock<IRoleService>();
        roles.Setup(x => x.SeedSystemRolesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        return roles;
    }

    private UserManagementService Users(
        bool passwordMatches = true,
        BeaconConfiguration? configuration = null,
        Mock<IRoleService>? roleService = null,
        SqlCapture? offline = null,
        List<BeaconRole>? roles = null)
    {
        var hasher = new Mock<IPasswordHasher>();
        hasher.Setup(x => x.HashPassword(It.IsAny<string>())).Returns(("hash", "salt"));
        hasher.Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(passwordMatches);
        var sets = new Dictionary<Type, object>
        {
            [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(_users, _saved),
            [typeof(BeaconRole)] = RecordingBeaconContext.MemorySet(roles ?? _roles, _saved),
            [typeof(BeaconUserRole)] = RecordingBeaconContext.MemorySet(_userRoles, _saved),
            [typeof(ApiKeyCredential)] = RecordingBeaconContext.MemorySet(_keys, _saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, _saved, offline));

        return new UserManagementService(
            factory.Object,
            hasher.Object,
            (roleService ?? new Mock<IRoleService>()).Object,
            configuration ?? new BeaconConfiguration(),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero)),
            _audit);
    }

    private (ApiKeyService Service, List<object> Saved) ApiKeys(BeaconActor caller)
    {
        _audit = new RecordingSecurityAudit(caller);
        var sets = new Dictionary<Type, object> { [typeof(ApiKeyCredential)] = RecordingBeaconContext.MemorySet(_keys, _saved) };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, _saved));
        var actors = new Mock<IBeaconActorAccessor>();
        actors.Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(caller);

        return (new ApiKeyService(
            factory.Object,
            Options.Create(new ApiKeyOptions()),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero)),
            _audit,
            actors.Object,
            NullLogger<ApiKeyService>.Instance), _saved);
    }
}
