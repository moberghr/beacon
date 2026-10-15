using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.UserSettings;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beacon.Tests.Unit;

/// <summary>
/// The server side of a <c>Beacon.Auth</c> cookie session, over a real cookie scheme on a test server (no database: the
/// user store is a fake). With Beacon user management on, the sign-in carries the stored roles and stamp (an inactive user
/// gets no session, nor does a user whose stamp changed since their password was verified), and a session is checked
/// against the user store at most every <c>RevalidateMinutes</c> (0: every request): a missing, disabled or archived user,
/// or a changed security stamp, signs it out and deletes the cookie; the session's roles become the stored ones
/// (identity-provider roles only within their claim lifetime). A realtime hub request is checked every time and reports the
/// next check as its expiry, and never renews the cookie (nor does the scheme's sliding expiration for it). However the
/// cookie is renewed, a session ends <c>AbsoluteLifetimeHours</c> after its sign-in, and its cookie never outlives that. A
/// host without Beacon user management keeps its own principals.
/// </summary>
[TestFixture]
public class BeaconSessionEventsTests
{
    private const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    private const string CookieName = "Beacon.Auth";
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    [TestCase("disabled")]
    [TestCase("archived")]
    [TestCase("missing")]
    public async Task AUserWhoIsNoLongerActive_IsSignedOut_AtTheNextRevalidation_NotBefore(string change)
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        await using var host = await SessionHost.StartAsync(store);
        await host.SignInAsync("ana");

        if (change == "missing")
        {
            store.Remove("ana");
        }
        else
        {
            store.Deactivate("ana");
        }

        host.Time.Advance(TimeSpan.FromMinutes(4));
        var withinInterval = await host.GetAsync("/test/me");
        host.Time.Advance(TimeSpan.FromMinutes(1));
        var revalidated = await host.GetAsync("/test/me");

        withinInterval.StatusCode.Should().Be(HttpStatusCode.OK, "the session is checked at most every five minutes");
        revalidated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        SessionCookieDeleted(revalidated).Should().BeTrue("a refused session's cookie is deleted");
        host.Logs.Should().Contain(x => x.StartsWith("Session ended:", StringComparison.Ordinal));
    }

    [Test]
    public async Task ASessionNamingNoStoredUser_IsLoggedAtWarning_WithAFingerprintOfItsId_NeverTheId()
    {
        var store = new FakeSessionStore().With("ana.secret-id", 7, "stamp-1", "Viewer");
        await using var host = await SessionHost.StartAsync(store);
        await host.SignInAsync("ana.secret-id");

        store.Remove("ana.secret-id");
        host.Time.Advance(TimeSpan.FromMinutes(5));
        var response = await host.GetAsync("/test/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var entry = host.Entries.Should().ContainSingle(x => x.Message.Contains("user_missing", StringComparison.Ordinal)).Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain(SubjectFingerprint.Of("ana.secret-id")).And.NotContain("ana.secret-id");
    }

    [Test]
    public async Task AUserWhoIsNoLongerActiveWhenTheCookieIsIssued_GetsNoSession()
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        store.Deactivate("ana");
        await using var host = await SessionHost.StartAsync(store);

        var signIn = await host.GetAsync("/test/sign-in?user=ana&remember=true");
        var after = await host.GetAsync("/test/me");

        (await signIn.Content.ReadAsStringAsync()).Should().Be("refused", "the endpoint is told the sign-in did not happen");
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a disabled or archived user's cookie carries no session");
        host.Logs.Should().Contain(x => x.Contains("user_disabled_or_archived_at_sign_in", StringComparison.Ordinal) && x.Contains("user 7", StringComparison.Ordinal));
    }

    [TestCase("disabled", "user_disabled_or_archived")]
    [TestCase("stamp", "security_stamp_changed")]
    public async Task ASessionEndedAtACheck_IsRecorded_AsTheUsers_WithItsReason(string change, string reason)
    {
        var store = new FakeSessionStore().With("ana.ext-id", 7, "stamp-1", "Viewer");
        await using var host = await SessionHost.StartAsync(store);
        await host.SignInAsync("ana.ext-id");
        if (change == "disabled")
        {
            store.Deactivate("ana.ext-id");
        }
        else
        {
            store.RenewStamp("ana.ext-id");
        }

        host.Time.Advance(TimeSpan.FromMinutes(5));
        (await host.GetAsync("/test/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        host.Audit.Single("ana.ext-id").Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.SessionEnded, ActorKind = SecurityAuditActorKinds.User, ActorUserId = (int?)7, ResourceId = "7", DetailsJson = $"{{\"reason\":\"{reason}\"}}" },
            x => x.ExcludingMissingMembers());
        host.Audit.Ways.Should().Equal("recorded");
    }

    [Test]
    public async Task ASessionNamingNoStoredUser_IsRecordedByAKeyedHashOfItsId_NeverTheId()
    {
        var store = new FakeSessionStore().With("ana.secret-id", 7, "stamp-1", "Viewer");
        await using var host = await SessionHost.StartAsync(store);
        await host.SignInAsync("ana.secret-id");
        store.Remove("ana.secret-id");

        host.Time.Advance(TimeSpan.FromMinutes(5));
        await host.GetAsync("/test/me");

        var ended = host.Audit.Single("ana.secret-id", SubjectFingerprint.Of("ana.secret-id"));
        ended.ActorKind.Should().Be(SecurityAuditActorKinds.Session);
        ended.ActorUserId.Should().BeNull();
        ended.CallerHash.Should().Be(SecurityAuditTestKit.CallerHasher.Hash(Core.SecurityAudit.SecurityAuditRecorder.SessionTenant, "ana.secret-id"));
        ended.DetailsJson.Should().Be("{\"reason\":\"user_missing\"}");
    }

    [Test]
    public async Task ASignInRefusedAsTheCookieIsIssued_IsRecordedOnce()
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        store.Deactivate("ana");
        await using var host = await SessionHost.StartAsync(store);

        await host.GetAsync("/test/sign-in?user=ana");

        host.Audit.Single().Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.SessionEnded, ActorUserId = (int?)7, DetailsJson = "{\"reason\":\"user_disabled_or_archived_at_sign_in\"}" },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task AChangedSecurityStamp_SignsOutTheSession()
    {
        var store = new FakeSessionStore().With("ana", 7, null, "Viewer");
        await using var host = await SessionHost.StartAsync(store);
        await host.SignInAsync("ana");

        host.Time.Advance(TimeSpan.FromMinutes(5));
        var unchanged = await host.GetAsync("/test/me");
        store.RenewStamp("ana");
        host.Time.Advance(TimeSpan.FromMinutes(5));
        var changed = await host.GetAsync("/test/me");

        unchanged.StatusCode.Should().Be(HttpStatusCode.OK, "a session signed in while the user had no stamp matches no stamp");
        changed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        SessionCookieDeleted(changed).Should().BeTrue();
    }

    [Test]
    public async Task TheSessionsRoles_AreTheStoredRoles_FromTheSignIn_AndAfterEachRevalidation_AndTheCookieCarriesThem()
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        await using var host = await SessionHost.StartAsync(store);
        await host.SignInAsync("ana", roles: "Admin,Viewer");

        var atSignIn = await host.RolesAsync();
        store.With("ana", 7, "stamp-1", "Editor");
        host.Time.Advance(TimeSpan.FromMinutes(5));
        var replaced = await host.RolesAsync();
        var afterwards = await host.RolesAsync();

        atSignIn.Should().Equal(["Viewer"], "the read that supplies the stamp at sign-in supplies the roles too");
        replaced.Should().Equal("Editor");
        afterwards.Should().Equal(["Editor"], "the renewed cookie carries the stored roles");
        host.Logs.Should().Contain(x => x.Contains("roles of user 7 replaced", StringComparison.Ordinal));
    }

    [TestCase(59, true)]
    [TestCase(61, false)]
    public async Task IdentityProviderMappedRoles_CountOnlyWithinTheirClaimLifetime(int minutesAfterSignIn, bool kept)
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        var mapping = new RoleMappingOptions();
        mapping.IdpRoles.Enabled = true;
        mapping.IdpRoles.ClaimLifetimeMinutes = 60;
        mapping.IdpRoles.Map["Beacon.Approvers"] = BeaconPermissions.Approver;
        var approverRole = mapping.RoleNameFor(BeaconPermissions.Approver);
        await using var host = await SessionHost.StartAsync(store, revalidateMinutes: 0, roleMapping: mapping);
        await host.SignInAsync("ana", roles: $"Viewer,{approverRole}", idpPermission: BeaconPermissions.Approver);

        host.Time.Advance(TimeSpan.FromMinutes(minutesAfterSignIn));
        var roles = await host.RolesAsync();

        if (kept)
        {
            roles.Should().BeEquivalentTo([approverRole, "Viewer"]);
        }
        else
        {
            roles.Should().Equal(["Viewer"], "the mapped role ends with the claim lifetime, as the permission accessor ends the permission");
        }
    }

    [Test]
    public async Task ARealtimeHubRequest_IsCheckedEveryTime_AndReportsTheNextCheckAsItsExpiry_WithoutRenewingTheCookie()
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        await using var host = await SessionHost.StartAsync(store);
        await host.SignInAsync("ana", remember: true);
        var cookie = host.Cookie;
        var lookups = store.Lookups;

        host.Time.Advance(TimeSpan.FromMinutes(1));
        var hub = await host.GetAsync("/beacon/api/hub/expiry");
        host.Time.Advance(TimeSpan.FromMinutes(3));
        var page = await host.GetAsync("/test/expiry");

        hub.StatusCode.Should().Be(HttpStatusCode.OK);
        DateTimeOffset.Parse(await hub.Content.ReadAsStringAsync(), CultureInfo.InvariantCulture)
            .Should().Be(Start.AddMinutes(6), "a hub connection closes when the session's next check is due");
        hub.Headers.Contains("Set-Cookie").Should().BeFalse("the shorter expiry is reported for the hub request only");
        host.Cookie.Should().Be(cookie);
        (store.Lookups - lookups).Should().Be(1, "the hub request was checked against the store though the interval had not passed");
        DateTimeOffset.Parse(await page.Content.ReadAsStringAsync(), CultureInfo.InvariantCulture)
            .Should().Be(Start.AddHours(12), "other requests still see the session's own expiry");
    }

    [Test]
    public async Task ARealtimeHubRequest_OfARevokedSession_IsRefused()
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        await using var host = await SessionHost.StartAsync(store);
        await host.SignInAsync("ana");

        store.RenewStamp("ana");
        host.Time.Advance(TimeSpan.FromMinutes(1));
        var hub = await host.GetAsync("/beacon/api/hub/expiry");

        hub.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a hub request does not wait for the revalidation interval");
    }

    [Test]
    public async Task ARealtimeHubRequest_PastTheSlidingMidpoint_DoesNotRenewTheCookie()
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        await using var host = await SessionHost.StartAsync(store);
        await host.SignInAsync("ana", remember: true);

        // Seven hours into a twelve-hour cookie: past half its window, where the scheme's sliding expiration renews it.
        host.Time.Advance(TimeSpan.FromHours(7));
        var hub = await host.GetAsync("/beacon/api/hub/expiry");
        var page = await host.GetAsync("/test/me");

        hub.StatusCode.Should().Be(HttpStatusCode.OK);
        hub.Headers.Contains("Set-Cookie").Should().BeFalse("a hub request never renews the cookie");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        CookieExpiry(page).Should().NotBeNull("other requests still renew it").And.BeOnOrBefore(Start.AddHours(12));
    }

    [Test]
    public async Task ARealtimeHubRequest_NeverRenewsACookieFromBeforeTheUpgrade_SoItEndsWithItsOriginalLifetime()
    {
        // A cookie from before sessions recorded their start: its lifetime counts from when it was issued (Start), and
        // only realtime requests use it. Twenty hours long, so its sliding midpoint comes before the absolute end.
        var store = new FakeSessionStore().With("ana", 7, null, "Viewer");
        await using var host = await SessionHost.StartAsync(store);
        host.IssueCookieWithoutSessionStart("ana", issuedAt: Start, expiresAt: Start.AddHours(20));
        var cookie = host.Cookie;

        host.Time.Advance(TimeSpan.FromHours(11));
        var pastTheMidpoint = await host.GetAsync("/beacon/api/hub/expiry");
        host.Time.Advance(TimeSpan.FromMinutes(59));
        var justBeforeTheEnd = await host.GetAsync("/beacon/api/hub/expiry");
        var cookieUntilTheEnd = host.Cookie;
        host.Time.Advance(TimeSpan.FromMinutes(1));
        var atTheEnd = await host.GetAsync("/beacon/api/hub/expiry");

        pastTheMidpoint.StatusCode.Should().Be(HttpStatusCode.OK);
        pastTheMidpoint.Headers.Contains("Set-Cookie").Should().BeFalse("a hub request never renews the cookie");
        justBeforeTheEnd.StatusCode.Should().Be(HttpStatusCode.OK);
        justBeforeTheEnd.Headers.Contains("Set-Cookie").Should().BeFalse();
        cookieUntilTheEnd.Should().Be(cookie);
        atTheEnd.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the session ends twelve hours after the cookie was issued");
    }

    [Test]
    public async Task TheSignIn_CarriesTheStampAsAClaimOfBeacon_AndSigningInAgainReplacesIt()
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        await using var host = await SessionHost.StartAsync(store);
        await host.SignInAsync("ana");

        var first = await host.StampClaimsAsync();
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await host.GetAsync("/test/change-stamp-and-refresh");
        var second = await host.StampClaimsAsync();

        first.Should().Equal("stamp-1");
        second.Should().ContainSingle().Which.Should().NotBe("stamp-1");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task IdentityProviderMappedRoles_StayOnlyWhileTheMappingIsOn(bool mappingOn)
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        var mapping = new RoleMappingOptions();
        mapping.IdpRoles.Enabled = mappingOn;
        mapping.IdpRoles.Map["Beacon.Approvers"] = BeaconPermissions.Approver;
        var approverRole = mapping.RoleNameFor(BeaconPermissions.Approver);
        await using var host = await SessionHost.StartAsync(store, roleMapping: mapping);
        await host.SignInAsync("ana", roles: $"Viewer,{approverRole}", idpPermission: BeaconPermissions.Approver);

        host.Time.Advance(TimeSpan.FromMinutes(5));
        var roles = await host.RolesAsync();

        if (mappingOn)
        {
            roles.Should().BeEquivalentTo([approverRole, "Viewer"], "the sign-in mapped the Approver role for this session");
        }
        else
        {
            roles.Should().Equal("Viewer");
        }
    }

    [TestCase(5, 1)]
    [TestCase(0, 3)]
    public async Task TheRevalidationInterval_IsHonoured(int revalidateMinutes, int lookupsAfterSignIn)
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        await using var host = await SessionHost.StartAsync(store, revalidateMinutes: revalidateMinutes);
        await host.SignInAsync("ana");
        var atSignIn = store.Lookups;

        await host.GetAsync("/test/me");
        host.Time.Advance(TimeSpan.FromMinutes(2));
        await host.GetAsync("/test/me");
        host.Time.Advance(TimeSpan.FromMinutes(3));
        await host.GetAsync("/test/me");

        atSignIn.Should().Be(1, "the sign-in reads the user's stamp");
        (store.Lookups - atSignIn).Should().Be(lookupsAfterSignIn);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task TheAbsoluteLifetime_EndsASessionInUse_WhateverRenewsIt(bool userManagement)
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        await using var host = await SessionHost.StartAsync(store, userManagement: userManagement);
        await host.SignInAsync("ana", remember: true);

        var answers = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            host.Time.Advance(TimeSpan.FromHours(2.5));
            answers.Add((await host.GetAsync("/test/me")).StatusCode);
        }

        // Past the end, the renewed cookie's own expiry (never later than the absolute lifetime) refuses it; a request at
        // the end itself is refused and logged by the session check (TheAbsoluteLifetime_EndsTheSession_Exactly...).
        answers.Should().Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Unauthorized],
            "the session ends twelve hours after the sign-in, though every request renewed it");
    }

    [Test]
    public async Task ARememberedSessionsCookie_ExpiresWithTheAbsoluteLifetime()
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        await using var host = await SessionHost.StartAsync(store);

        var response = await host.SignInAsync("ana", remember: true);

        var cookie = response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith($"{CookieName}=", StringComparison.Ordinal));
        var expires = DateTimeOffset.Parse(
            cookie.Split(';').Select(x => x.Trim()).Single(x => x.StartsWith("expires=", StringComparison.OrdinalIgnoreCase))["expires=".Length..],
            CultureInfo.InvariantCulture);
        expires.Should().Be(Start.AddHours(12), "remember-me asked for 30 days, the absolute lifetime is 12 hours");
    }

    [Test]
    public async Task WithoutBeaconUserManagement_TheHostsPrincipalIsKept_AndTheStoreIsNotAsked()
    {
        var store = new FakeSessionStore();
        await using var host = await SessionHost.StartAsync(store, userManagement: false);
        await host.SignInAsync("host-user", roles: "Admin");

        host.Time.Advance(TimeSpan.FromMinutes(30));
        var roles = await host.RolesAsync();

        roles.Should().Equal("Admin");
        store.Lookups.Should().Be(0);
    }

    [Test]
    public async Task SigningTheSessionInAgain_CarriesTheNewStamp_SoTheCallersOwnSessionSurvives()
    {
        // What changing one's own password does: the stamp changes, and the endpoint signs this session in again.
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        await using var host = await SessionHost.StartAsync(store);
        await host.SignInAsync("ana");
        var other = host.Cookie;

        host.Time.Advance(TimeSpan.FromMinutes(1));
        var refreshed = await host.GetAsync("/test/change-stamp-and-refresh");
        host.Time.Advance(TimeSpan.FromMinutes(5));
        var own = await host.GetAsync("/test/me");
        host.Cookie = other;
        var stale = await host.GetAsync("/test/me");

        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        own.StatusCode.Should().Be(HttpStatusCode.OK, "this session was signed in again with the new stamp");
        stale.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "every other session of the user ends");
    }

    [Test]
    public async Task APasswordSignIn_WhoseUsersStampChangedSinceThePasswordWasVerified_GetsNoSession()
    {
        // The password was verified with stamp-1; by the time the cookie is issued, the stored stamp is stamp-2 (an
        // administrator reset the password in between).
        var store = new FakeSessionStore().With("ana", 7, "stamp-2", "Viewer");
        await using var host = await SessionHost.StartAsync(store);

        var signIn = await host.GetAsync("/test/sign-in?user=ana&remember=true&verified=true&verifiedStamp=stamp-1");
        var after = await host.GetAsync("/test/me");

        (await signIn.Content.ReadAsStringAsync()).Should().Be("refused", "the endpoint is told the sign-in did not happen");
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "no session carries the newer stamp");
        host.Logs.Should().Contain(x => x.Contains("security_stamp_changed_at_sign_in", StringComparison.Ordinal) && x.Contains("user 7", StringComparison.Ordinal));
    }

    // A user without a stamp was verified with none: a missing stamp and an empty one are the same.
    [TestCase("stamp-1", "stamp-1")]
    [TestCase(null, "")]
    public async Task APasswordSignIn_WithTheStampThePasswordWasVerifiedWith_IsSignedInWithIt(string? stored, string verified)
    {
        var store = new FakeSessionStore().With("ana", 7, stored, "Viewer");
        await using var host = await SessionHost.StartAsync(store);

        var signIn = await host.GetAsync($"/test/sign-in?user=ana&verified=true&verifiedStamp={verified}");
        var stamps = await host.StampClaimsAsync();

        signIn.StatusCode.Should().Be(HttpStatusCode.OK);
        (await signIn.Content.ReadAsStringAsync()).Should().NotBe("refused");
        stamps.Should().Equal(stored ?? string.Empty);
    }

    [Test]
    public async Task ASignInWithoutAVerifiedPassword_AsSingleSignOnSignsIn_CarriesTheStoredStamp()
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-2", "Viewer");
        await using var host = await SessionHost.StartAsync(store);

        await host.SignInAsync("ana");
        var stamps = await host.StampClaimsAsync();

        stamps.Should().Equal("stamp-2");
    }

    [Test]
    public async Task APasswordVerifiedForAnotherUser_DoesNotDecideThisSignIn()
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-2", "Viewer");
        await using var host = await SessionHost.StartAsync(store);

        await host.GetAsync("/test/sign-in?user=ana&verified=true&verifiedUser=bob&verifiedStamp=stamp-1");
        var stamps = await host.StampClaimsAsync();

        stamps.Should().Equal("stamp-2");
    }

    [Test]
    public async Task SigningTheSessionInAgain_AfterAnotherStampChangeCameInBetween_EndsTheSession()
    {
        // Changing one's own password writes a new stamp; a sign-out everywhere lands before the session is signed in again.
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        await using var host = await SessionHost.StartAsync(store);
        await host.SignInAsync("ana");

        host.Time.Advance(TimeSpan.FromMinutes(1));
        var refreshed = await host.GetAsync("/test/change-password-and-refresh?changedAgain=true");
        var after = await host.GetAsync("/test/me");

        (await refreshed.Content.ReadAsStringAsync()).Should().Be("refused");
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the session never adopts the stamp of the later change");
        host.Logs.Should().Contain(x => x.Contains("security_stamp_changed_at_sign_in", StringComparison.Ordinal));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TheChangePasswordEndpoint_KeepsTheSession_OrAsksToSignInAgain_WhenTheStampChangedAgainMeanwhile(bool changedAgain)
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        IHttpContextAccessor? requests = null;
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<ChangeOwnPasswordCommand>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                // What the password change does: a new stamp, written and recorded as the one this session goes on with.
                store.RenewStamp("ana");
                requests!.HttpContext!.RequestServices.GetRequiredService<SessionStampExpectation>().Record("ana", store.StampOf("ana"));
                if (changedAgain)
                {
                    store.RenewStamp("ana");
                }

                return Task.CompletedTask;
            });
        await using var host = await SessionHost.StartAsync(store, mediator: mediator.Object);
        requests = host.Services.GetRequiredService<IHttpContextAccessor>();
        await host.SignInAsync("ana");

        host.Time.Advance(TimeSpan.FromMinutes(1));
        var changed = await host.PostAsync("/beacon/api/user-settings/change-password", new ChangeOwnPasswordCommand("old", "New-password-1!"));
        var after = await host.GetAsync("/test/me");

        if (changedAgain)
        {
            changed.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the UI signs the user in again");
            (await changed.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail.Should().Be(UserSettingsEndpoints.SignInAgainDetail);
            after.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        else
        {
            changed.StatusCode.Should().Be(HttpStatusCode.NoContent);
            after.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [TestCase("stamp-1", false)]
    [TestCase("stamp-2", true)]
    public async Task TheLoginEndpoint_AnswersASignInWhoseUsersStampChangedMeanwhile_LikeAFailedLogin(string storedAtSignIn, bool refused)
    {
        // The password is verified against the stored user, whose stamp is then stamp-1; the cookie is issued against the
        // session store, which by then may hold stamp-2 (an administrator reset the password in between).
        var store = new FakeSessionStore().With("ext-ana", 7, storedAtSignIn, "Viewer");
        var passwordUser = new BeaconUser
        {
            Id = 7,
            ExternalId = "ext-ana",
            UserName = "ana",
            IsInternalUser = true,
            IsEnabled = true,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            SecurityStamp = "stamp-1"
        };
        await using var host = await SessionHost.StartAsync(store, passwordUsers: [passwordUser]);

        var login = await host.PostAsync("/beacon/api/auth/login", new LoginRequest("ana", "right"));
        var after = await host.GetAsync("/test/me");

        if (refused)
        {
            login.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Error.Should().Be("Invalid username or password.");
            after.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the refused sign-in left no session cookie");
            host.Logs.Should().Contain(x => x.Contains("security_stamp_changed_at_sign_in", StringComparison.Ordinal));
        }
        else
        {
            login.StatusCode.Should().Be(HttpStatusCode.OK);
            after.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task ASessionSignedInBeforeSessionsRecordedTheirStart_CountsFromItsLastRenewal()
    {
        var time = new FakeTimeProvider(Start);
        using var services = AuthenticationServices(time);

        var recent = await ValidateLegacyAsync(services, time, issuedHoursAgo: 1);
        var old = await ValidateLegacyAsync(services, time, issuedHoursAgo: 13);

        recent.Principal.Should().NotBeNull();
        recent.ShouldRenew.Should().BeTrue("the start is recorded in the renewed cookie");
        recent.Properties.Items[BeaconSessionEvents.SignedInAtItem].Should().Be(Start.AddHours(-1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        recent.Properties.ExpiresUtc.Should().Be(Start.AddHours(11), "the expiry this request reports never passes the absolute lifetime");
        old.Principal.Should().BeNull();
    }

    // A ticket from before the upgrade records no start, no check and no stamp: with user management on, its first request
    // checks it against the store. A user without a stamp matches it; one whose stamp was set since does not.
    [TestCase("active-without-stamp", true)]
    [TestCase("active-with-stamp", false)]
    [TestCase("disabled", false)]
    [TestCase("missing", false)]
    public async Task ATicketFromBeforeTheUpgrade_IsCheckedAgainstTheStore_AtItsFirstRequest(string user, bool kept)
    {
        var time = new FakeTimeProvider(Start);
        var store = new FakeSessionStore();
        if (user != "missing")
        {
            store.With("ana", 7, user == "active-with-stamp" ? "stamp-1" : null, "Viewer");
        }

        if (user == "disabled")
        {
            store.Deactivate("ana");
        }

        using var services = AuthenticationServices(time, store);

        var context = await ValidateLegacyAsync(services, time, issuedHoursAgo: 1);

        store.Lookups.Should().Be(1, "the ticket was never checked");
        (context.Principal != null).Should().Be(kept);
    }

    // A check recorded later than now (a clock that went back, or another node's clock ahead) is no reason to skip one.
    [TestCase(-1, 0)]
    [TestCase(10, 1)]
    public async Task ACheckRecordedInTheFuture_DoesNotPostponeTheNextOne(int checkedMinutesFromNow, int lookups)
    {
        var time = new FakeTimeProvider(Start);
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        using var services = AuthenticationServices(time, store);
        var properties = new AuthenticationProperties
        {
            IssuedUtc = Start.AddHours(-1),
            ExpiresUtc = Start.AddHours(11),
            AllowRefresh = true,
            Items =
            {
                [BeaconSessionEvents.SignedInAtItem] = Start.AddHours(-1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                [BeaconSessionEvents.ValidatedAtItem] = Start.AddMinutes(checkedMinutesFromNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                [BeaconSessionEvents.StampItem] = "stamp-1"
            }
        };

        var context = await ValidateAsync(services, time, properties);

        context.Principal.Should().NotBeNull();
        store.Lookups.Should().Be(lookups);
    }

    [Test]
    public async Task TheTimeOfACheck_IsKeptInTheCookie_SoTheNextOneIsAWholeIntervalLater()
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        await using var host = await SessionHost.StartAsync(store);
        await host.SignInAsync("ana");
        var atSignIn = store.Lookups;

        host.Time.Advance(TimeSpan.FromMinutes(5));
        await host.GetAsync("/test/me");
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await host.GetAsync("/test/me");
        host.Time.Advance(TimeSpan.FromMinutes(3));
        await host.GetAsync("/test/me");
        var withinTheNextInterval = store.Lookups - atSignIn;
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await host.GetAsync("/test/me");

        withinTheNextInterval.Should().Be(1, "the check at five minutes was recorded in the renewed cookie");
        (store.Lookups - atSignIn).Should().Be(2, "the next check is due five minutes after the last one");
    }

    // A cookie scheme without sliding expiration keeps its expiry when a check renews the cookie; with sliding expiration,
    // the renewed cookie still ends with the absolute lifetime.
    [TestCase(false)]
    [TestCase(true)]
    public async Task ACookieRenewedByACheck_NeverOutlivesTheAbsoluteLifetime_AndANonSlidingOneKeepsItsExpiry(bool sliding)
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        await using var host = await SessionHost.StartAsync(store, sliding: sliding);
        var signIn = await host.SignInAsync("ana", remember: true);

        host.Time.Advance(TimeSpan.FromHours(7));
        var renewed = await host.GetAsync("/test/me");

        renewed.StatusCode.Should().Be(HttpStatusCode.OK);
        var renewedExpiry = CookieExpiry(renewed);
        renewedExpiry.Should().NotBeNull("the check renewed the cookie");
        renewedExpiry.Should().BeOnOrBefore(Start.AddHours(12));
        if (!sliding)
        {
            renewedExpiry.Should().Be(CookieExpiry(signIn), "a scheme that does not slide is not extended by a check");
        }
    }

    [Test]
    public async Task TheAbsoluteLifetime_EndsTheSession_ExactlyTwelveHoursAfterTheSignIn()
    {
        var store = new FakeSessionStore().With("ana", 7, "stamp-1", "Viewer");
        await using var host = await SessionHost.StartAsync(store);
        await host.SignInAsync("ana", remember: true);

        host.Time.Advance(TimeSpan.FromHours(12) - TimeSpan.FromSeconds(1));
        var justBefore = await host.GetAsync("/test/me");
        host.Time.Advance(TimeSpan.FromSeconds(1));
        var atTheEnd = await host.GetAsync("/test/me");

        justBefore.StatusCode.Should().Be(HttpStatusCode.OK);
        atTheEnd.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        host.Logs.Should().Contain(x => x.Contains("absolute_lifetime", StringComparison.Ordinal));
    }

    [Test]
    public void AddBeaconCookieAuthentication_WiresTheSessionEvents()
    {
        var services = new ServiceCollection();
        services.TryAddSingleton<ISecurityAuditRecorder>(new RecordingSecurityAudit());
        services.AddLogging();
        services.AddBeaconCookieAuthentication();
        using var provider = services.BuildServiceProvider();
        var options = new CookieAuthenticationOptions();

        foreach (var configure in provider.GetServices<IConfigureOptions<CookieAuthenticationOptions>>())
        {
            (configure as IConfigureNamedOptions<CookieAuthenticationOptions>)?.Configure(Scheme, options);
        }

        options.Events.OnSigningIn.Should().Be((Func<CookieSigningInContext, Task>)BeaconSessionEvents.SigningInAsync);
        options.Events.OnValidatePrincipal.Should().Be((Func<CookieValidatePrincipalContext, Task>)BeaconSessionEvents.ValidatePrincipalAsync);
        options.Events.OnCheckSlidingExpiration.Should().Be((Func<CookieSlidingExpirationContext, Task>)BeaconSessionEvents.CheckSlidingExpirationAsync);
        options.Cookie.HttpOnly.Should().BeTrue();
        options.Cookie.SameSite.Should().Be(SameSiteMode.Lax);
        options.Cookie.SecurePolicy.Should().Be(CookieSecurePolicy.SameAsRequest);
    }

    private static bool SessionCookieDeleted(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(x => x.StartsWith($"{CookieName}=;", StringComparison.Ordinal) && x.Contains("1970", StringComparison.Ordinal));
    }

    // The expiry a response's session cookie carries, or null when it sets no session cookie with one.
    private static DateTimeOffset? CookieExpiry(HttpResponseMessage response)
    {
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.FirstOrDefault(x => x.StartsWith($"{CookieName}=", StringComparison.Ordinal))
            : null;
        var expires = cookie?
            .Split(';')
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith("expires=", StringComparison.OrdinalIgnoreCase));

        return expires == null ? null : DateTimeOffset.Parse(expires["expires=".Length..], CultureInfo.InvariantCulture);
    }

    // The cookie scheme's services; with a store, Beacon user management is on and sessions are checked against it.
    private static ServiceProvider AuthenticationServices(TimeProvider time, FakeSessionStore? store = null)
    {
        var services = new ServiceCollection();
        services.TryAddSingleton<ISecurityAuditRecorder>(new RecordingSecurityAudit());
        services.AddLogging();
        services.AddSingleton(time);
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAuthentication(Scheme).AddCookie();
        if (store != null)
        {
            var configuration = new BeaconConfiguration();
            configuration.UserManagement.Enabled = true;
            services.AddSingleton(configuration);
            services.AddSingleton<IUserSessionStore>(store);
        }

        return services.BuildServiceProvider();
    }

    private static Task<CookieValidatePrincipalContext> ValidateLegacyAsync(IServiceProvider services, FakeTimeProvider time, int issuedHoursAgo)
    {
        return ValidateAsync(services, time, new AuthenticationProperties
        {
            IssuedUtc = time.GetUtcNow().AddHours(-issuedHoursAgo),
            ExpiresUtc = time.GetUtcNow().AddHours(24 - issuedHoursAgo),
            AllowRefresh = true
        });
    }

    // Validates a cookie ticket for "ana" with the given properties, as the cookie handler would on a request.
    private static async Task<CookieValidatePrincipalContext> ValidateAsync(IServiceProvider services, FakeTimeProvider time, AuthenticationProperties properties)
    {
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ana")], Scheme));
        var context = new CookieValidatePrincipalContext(
            httpContext,
            new AuthenticationScheme(Scheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions { TimeProvider = time, SlidingExpiration = true },
            new AuthenticationTicket(principal, properties, Scheme));

        await BeaconSessionEvents.ValidatePrincipalAsync(context);

        return context;
    }

    // A test server with the Beacon.Auth cookie scheme and the session events, a fake user store and a fake clock. Keeps
    // the session cookie between requests as a browser would.
    private sealed class SessionHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private SessionHost(WebApplication app, FakeTimeProvider time, LogRecorder logs, RecordingSecurityAudit audit)
        {
            _app = app;
            _client = app.GetTestClient();
            Time = time;
            LogRecorder = logs;
            Audit = audit;
        }

        public FakeTimeProvider Time { get; }

        /// <summary>The security audit events the host's requests recorded.</summary>
        public RecordingSecurityAudit Audit { get; }

        public string? Cookie { get; set; }

        public IServiceProvider Services => _app.Services;

        public IEnumerable<string> Logs => LogRecorder.Entries.Select(x => x.Message);

        public IEnumerable<RecordedLog> Entries => LogRecorder.Entries;

        private LogRecorder LogRecorder { get; }

        public static async Task<SessionHost> StartAsync(
            FakeSessionStore store,
            bool userManagement = true,
            int revalidateMinutes = 5,
            RoleMappingOptions? roleMapping = null,
            bool sliding = true,
            IMediator? mediator = null,
            List<BeaconUser>? passwordUsers = null)
        {
            var time = new FakeTimeProvider(Start);
            var logs = new LogRecorder();
            var audit = new RecordingSecurityAudit();
            var configuration = new BeaconConfiguration();
            configuration.UserManagement.Enabled = userManagement;

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Services.TryAddSingleton<ISecurityAuditRecorder>(new RecordingSecurityAudit());
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Services.AddSingleton(configuration);
            builder.Services.AddSingleton<TimeProvider>(time);
            builder.Services.AddSingleton<IUserSessionStore>(store);
            builder.Services.AddSingleton<ISecurityAuditRecorder>(audit);
            builder.Services.AddSingleton(Options.Create(new SessionLifetimeOptions { RevalidateMinutes = revalidateMinutes, AbsoluteLifetimeHours = 12 }));
            builder.Services.AddSingleton(Options.Create(roleMapping ?? new RoleMappingOptions()));
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddScoped<SessionStampExpectation>();
            builder.Services
                .AddAuthentication(Scheme)
                .AddCookie(x =>
                {
                    x.Cookie.Name = CookieName;
                    x.SlidingExpiration = sliding;
                    x.Events.OnSigningIn = BeaconSessionEvents.SigningInAsync;
                    x.Events.OnValidatePrincipal = BeaconSessionEvents.ValidatePrincipalAsync;
                    x.Events.OnCheckSlidingExpiration = BeaconSessionEvents.CheckSlidingExpirationAsync;
                });
            if (mediator != null)
            {
                builder.Services.AddSingleton(mediator);
                builder.Services.AddHttpContextAccessor();
            }

            if (passwordUsers != null)
            {
                AddPasswordLogin(builder.Services, configuration, passwordUsers);
            }

            var app = builder.Build();
            app.UseAuthentication();
            if (mediator != null)
            {
                app.MapGroup("/beacon/api").MapUserSettingsEndpoints();
            }

            if (passwordUsers != null)
            {
                app.MapLoginEndpoints("/beacon", configuration);
            }

            // verified: a password of verifiedUser (else of user) was verified on this request, with verifiedStamp.
            app.MapGet("/test/sign-in", async (
                HttpContext context,
                string user,
                string? roles,
                bool? remember,
                string? idpPermission,
                bool? verified,
                string? verifiedUser,
                string? verifiedStamp) =>
            {
                if (verified == true)
                {
                    context.RequestServices.GetRequiredService<SessionStampExpectation>().Record(verifiedUser ?? user, verifiedStamp);
                }

                var now = time.GetUtcNow();
                var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user), new Claim(ClaimTypes.Name, user)], Scheme);
                foreach (var role in (roles ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    identity.AddClaim(new Claim(ClaimTypes.Role, role));
                }

                if (idpPermission != null)
                {
                    identity.AddClaim(new Claim(BeaconClaims.IdpPermission, idpPermission, ClaimValueTypes.String, BeaconClaims.Issuer));
                    identity.AddClaim(new Claim(
                        BeaconClaims.IdpPermissionsIssuedAt,
                        now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                        ClaimValueTypes.Integer64,
                        BeaconClaims.Issuer));
                }

                await context.SignInAsync(Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties
                {
                    IsPersistent = remember == true,
                    ExpiresUtc = remember == true ? now.AddDays(30) : now.AddHours(24),
                    AllowRefresh = true
                });

                return BeaconSessionEvents.WasSignInRefused(context) ? Results.Text("refused") : Results.Ok();
            });
            app.MapGet("/test/me", (HttpContext context) => context.User.Identity?.IsAuthenticated == true
                ? Results.Ok(context.User.FindAll(ClaimTypes.Role).Select(x => x.Value).Order(StringComparer.Ordinal).ToArray())
                : Results.Unauthorized());
            app.MapGet("/test/stamp", (HttpContext context) => context.User.FindAll(BeaconClaims.SessionStamp)
                .Where(x => x.Issuer == BeaconClaims.Issuer)
                .Select(x => x.Value)
                .ToArray());
            app.MapGet("/test/change-stamp-and-refresh", async (HttpContext context) =>
            {
                store.RenewStamp(context.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
                await BeaconSessionEvents.RefreshSignInAsync(context);
            });

            // As changing one's own password does: a new stamp, recorded as the one the session is signed in again with;
            // changedAgain: another change of the stamp lands in between.
            app.MapGet("/test/change-password-and-refresh", async (HttpContext context, bool? changedAgain) =>
            {
                var externalId = context.User.FindFirst(ClaimTypes.NameIdentifier)!.Value;
                store.RenewStamp(externalId);
                context.RequestServices.GetRequiredService<SessionStampExpectation>().Record(externalId, store.StampOf(externalId));
                if (changedAgain == true)
                {
                    store.RenewStamp(externalId);
                }

                await BeaconSessionEvents.RefreshSignInAsync(context);

                return BeaconSessionEvents.WasSignInRefused(context) ? Results.Text("refused") : Results.Ok();
            });

            // The expiry a request's cookie authentication reports: on the hub's route, and elsewhere.
            app.MapGet("/beacon/api/hub/expiry", (Func<HttpContext, Task<IResult>>)ReportExpiryAsync);
            app.MapGet("/test/expiry", (Func<HttpContext, Task<IResult>>)ReportExpiryAsync);
            await app.StartAsync();

            return new SessionHost(app, time, logs, audit);
        }

        public async Task<HttpResponseMessage> SignInAsync(string user, string? roles = null, bool remember = false, string? idpPermission = null)
        {
            var query = $"/test/sign-in?user={user}&remember={remember}";
            if (roles != null)
            {
                query += $"&roles={Uri.EscapeDataString(roles)}";
            }

            if (idpPermission != null)
            {
                query += $"&idpPermission={idpPermission}";
            }

            var response = await GetAsync(query);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            return response;
        }

        public async Task<string[]> StampClaimsAsync()
        {
            var response = await GetAsync("/test/stamp");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            return (await response.Content.ReadFromJsonAsync<string[]>())!;
        }

        public async Task<string[]> RolesAsync()
        {
            var response = await GetAsync("/test/me");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            return (await response.Content.ReadFromJsonAsync<string[]>())!;
        }

        // A session cookie as signed in before sessions recorded their start (and stamp): only the scheme's own properties.
        public void IssueCookieWithoutSessionStart(string user, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
        {
            var options = _app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(Scheme);
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user), new Claim(ClaimTypes.Name, user)], Scheme);
            var properties = new AuthenticationProperties
            {
                IssuedUtc = issuedAt,
                ExpiresUtc = expiresAt,
                AllowRefresh = true
            };

            Cookie = options.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(identity), properties, Scheme));
        }

        public Task<HttpResponseMessage> GetAsync(string path)
        {
            return SendAsync(new HttpRequestMessage(HttpMethod.Get, path));
        }

        public Task<HttpResponseMessage> PostAsync<T>(string path, T body)
        {
            return SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) });
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.DisposeAsync();
        }

        // Beacon's password login over the database provider, its users served without a database; each request's
        // verified stamp is recorded in that request's expectation, as Beacon registers it.
        private static void AddPasswordLogin(IServiceCollection services, BeaconConfiguration configuration, List<BeaconUser> users)
        {
            var hasher = new Mock<IPasswordHasher>();
            hasher
                .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<string, string, string>((password, hash, _) => password == "right" && hash == "hash");
            var saved = new List<object>();
            var sets = new Dictionary<Type, object> { [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(users, saved) };
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));

            services.AddScoped<IUserManagementService>(x => new UserManagementService(
                factory.Object,
                hasher.Object,
                Mock.Of<IRoleService>(),
                configuration,
                TimeProvider.System,
                new RecordingSecurityAudit(),
                null,
                x.GetRequiredService<SessionStampExpectation>()));
            services.AddScoped<IBeaconAuthenticationProvider, DatabaseAuthenticationProvider>();
            services.AddSingleton(new Mock<IAntiforgery>().Object);
            services.AddSingleton(new LoginRateLimiter());
        }

        // Sends the request (and disposes it) with the session cookie, keeping the cookie the response sets.
        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            using var sent = request;
            if (Cookie != null)
            {
                sent.Headers.Add("Cookie", $"{CookieName}={Cookie}");
            }

            var response = await _client.SendAsync(sent);
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                var session = cookies.FirstOrDefault(x => x.StartsWith($"{CookieName}=", StringComparison.Ordinal));
                if (session != null)
                {
                    var value = session.Split(';')[0][(CookieName.Length + 1)..];
                    Cookie = value.Length == 0 ? null : value;
                }
            }

            return response;
        }

        private static async Task<IResult> ReportExpiryAsync(HttpContext context)
        {
            var result = await context.AuthenticateAsync(Scheme);

            return result.Succeeded
                ? Results.Text(result.Properties!.ExpiresUtc!.Value.ToString("O", CultureInfo.InvariantCulture))
                : Results.Unauthorized();
        }
    }

    // The user store a session reads: users by external id, counting the lookups.
    private sealed class FakeSessionStore : IUserSessionStore
    {
        private readonly Dictionary<string, SessionUser> _users = new(StringComparer.Ordinal);

        public int Lookups { get; private set; }

        public FakeSessionStore With(string externalId, int id, string? stamp, params string[] roles)
        {
            _users[externalId] = new SessionUser { Id = id, IsActive = true, SecurityStamp = stamp, RoleNames = roles };

            return this;
        }

        public void Deactivate(string externalId)
        {
            var user = _users[externalId];
            _users[externalId] = new SessionUser { Id = user.Id, IsActive = false, SecurityStamp = user.SecurityStamp, RoleNames = user.RoleNames };
        }

        public void Remove(string externalId)
        {
            _users.Remove(externalId);
        }

        public string? StampOf(string externalId)
        {
            return _users[externalId].SecurityStamp;
        }

        public void RenewStamp(string externalId)
        {
            var user = _users[externalId];
            _users[externalId] = new SessionUser { Id = user.Id, IsActive = user.IsActive, SecurityStamp = SecurityStamps.New(), RoleNames = user.RoleNames };
        }

        public Task<SessionUser?> FindAsync(string externalId, CancellationToken cancellationToken)
        {
            Lookups++;

            return Task.FromResult(_users.GetValueOrDefault(externalId));
        }

        public Task<bool> RenewStampAsync(int userId, CancellationToken cancellationToken)
        {
            var user = _users.FirstOrDefault(x => x.Value.Id == userId);
            if (user.Value == null)
            {
                return Task.FromResult(false);
            }

            RenewStamp(user.Key);

            return Task.FromResult(true);
        }
    }
}
