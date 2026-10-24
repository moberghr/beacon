using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.SecurityAudit;

namespace Beacon.Api.Authentication;

/// <summary>
/// The security audit events of signing in and out, written on their own (a sign-in has no unit of work to join) and never
/// failing the request they record. The actor is always named here: while a request signs in, its caller is not resolved
/// yet, and resolving it early would fix it for the rest of the request. Users are named by their <c>Users.Id</c>, a
/// subject without one by a keyed hash; never by the name typed, an e-mail address or an external id. A failed password
/// sign-in names the attempt by a keyed hash of the name typed (<see cref="LoginTenant"/>), so attempts on one name can be
/// told apart from attempts spread over many without the name being stored. The recorder is part of
/// <c>AddBeaconServices</c>: without it these events throw instead of going unrecorded.
/// </summary>
internal static class SignInAudit
{
    /// <summary>The tenant a typed sign-in name is hashed under, apart from sessions' and identity providers'.</summary>
    internal const string LoginTenant = "beacon-login";

    internal const string PasswordChangeRequiredReason = "password_change_required";

    /// <summary>A password sign-in refused because the user is locked out after too many wrong passwords.</summary>
    internal const string LockedOutReason = "locked_out";

    /// <summary>
    /// A password sign-in that did not succeed: the user the name resolved to, when it did, and the attempt named by a keyed
    /// hash of <paramref name="typedName"/> (trimmed, ignoring case), never the name itself.
    /// </summary>
    public static Task PasswordFailedAsync(HttpContext context, AuthenticationResult result, string? typedName)
    {
        var refused = result.LocalLoginRefused || result.LockedOut;
        var reason = result.LockedOut
            ? LockedOutReason
            : result.LocalLoginRefused ? "local_login_off" : "invalid_credentials";
        var recorder = Recorder(context);
        var attempt = string.IsNullOrWhiteSpace(typedName)
            ? SecurityAuditActor.Anonymous
            : SecurityAuditActor.OfCaller(recorder.CallerHashOf(LoginTenant, typedName.Trim().ToLowerInvariant()));

        return recorder.RecordAsync(
            new SecurityAuditEntry(refused ? SecurityAuditActions.PasswordSignInRefused : SecurityAuditActions.PasswordSignInFailed)
                .On(SecurityAuditResources.User, result.BeaconUserId)
                .Ending(refused ? SecurityAuditOutcomes.Refused : SecurityAuditOutcomes.Failure)
                .By(attempt)
                .With(new SecurityAuditDetails().AddCode("reason", reason)),
            context.RequestAborted);
    }

    /// <summary>
    /// A password the provider accepted, for a user who must choose a new one first (an administrator set it): no session
    /// is issued, so the sign-in is refused by rule.
    /// </summary>
    public static Task PasswordChangeRequiredAsync(HttpContext context, AuthenticationResult result)
    {
        return RecordAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.PasswordSignInRefused)
                .On(SecurityAuditResources.User, result.BeaconUserId)
                .Ending(SecurityAuditOutcomes.Refused)
                .By(SecurityAuditActor.Anonymous)
                .With(new SecurityAuditDetails().AddCode("reason", PasswordChangeRequiredReason)));
    }

    /// <summary>A password the provider accepted, refused because local login is off and the account is no break-glass account.</summary>
    public static Task PasswordRefusedAsync(HttpContext context, AuthenticationResult result)
    {
        return RecordAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.PasswordSignInRefused)
                .On(SecurityAuditResources.User, result.BeaconUserId)
                .Ending(SecurityAuditOutcomes.Refused)
                .By(SecurityAuditActor.Anonymous)
                .With(new SecurityAuditDetails().AddCode("reason", "local_login_off")));
    }

    /// <summary>A password sign-in that issued a session; a break-glass account's while local login is off is its own action.</summary>
    public static Task SignedInAsync(HttpContext context, AuthenticationResult result, bool breakGlass)
    {
        var actor = result.BeaconUserId is { } userId
            ? SecurityAuditActor.OfUser(userId)
            : SessionCaller(context, result.User?.UserId);

        return RecordAsync(
            context,
            new SecurityAuditEntry(breakGlass ? SecurityAuditActions.BreakGlassSignedIn : SecurityAuditActions.PasswordSignInSucceeded)
                .On(SecurityAuditResources.User, result.BeaconUserId)
                .By(actor));
    }

    /// <summary>
    /// The request's session signed out, by its own user (the caller); nothing for an anonymous request. Resolving the
    /// caller never fails the sign-out: when it cannot be read, the event names no user, and the recorder, which resolves
    /// the caller again, logs the event it cannot write.
    /// </summary>
    public static async Task SignedOutAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        int? userId = null;
        try
        {
            userId = (await context.RequestServices.GetRequiredService<IBeaconActorAccessor>().GetCurrentAsync(context.RequestAborted)).BeaconUserId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(SignInAudit)).LogWarning(
                "The signing-out caller could not be resolved for the security audit ({ExceptionType}); the event names no user",
                ex.GetType().Name);
        }

        await RecordAsync(context, new SecurityAuditEntry(SecurityAuditActions.SignedOut).On(SecurityAuditResources.User, userId));
    }

    /// <summary>A session ended by Beacon: refused at sign-in or at a check against the user store.</summary>
    public static Task SessionEndedAsync(HttpContext context, string reason, int? userId, string? presentedId)
    {
        return RecordAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.SessionEnded)
                .On(SecurityAuditResources.User, userId)
                .By(userId is { } id ? SecurityAuditActor.OfUser(id) : SessionCaller(context, presentedId))
                .With(new SecurityAuditDetails().AddCode("reason", reason)));
    }

    /// <summary>A single sign-on the identity provider authenticated and Beacon admitted, as user <paramref name="userId"/>.</summary>
    public static Task SsoSignedInAsync(HttpContext context, int userId)
    {
        return RecordAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.SsoSignedIn)
                .On(SecurityAuditResources.User, userId)
                .By(SecurityAuditActor.OfUser(userId)));
    }

    /// <summary>A single sign-on Beacon refused; the subject is named by its keyed hash under its tenant (or issuer).</summary>
    public static Task SsoRefusedAsync(HttpContext context, string reason, string tenantOrIssuer, string subject)
    {
        var recorder = Recorder(context);

        return recorder.RecordAsync(
            new SecurityAuditEntry(SecurityAuditActions.SsoRefused)
                .Ending(SecurityAuditOutcomes.Refused)
                .By(SecurityAuditActor.OfCaller(recorder.CallerHashOf(tenantOrIssuer, subject)))
                .With(new SecurityAuditDetails().AddCode("reason", reason)),
            context.RequestAborted);
    }

    // A caller without a Beacon user (a host's own sign-in), named by a keyed hash of the id it presents.
    private static SecurityAuditActor SessionCaller(HttpContext context, string? presentedId)
    {
        if (string.IsNullOrEmpty(presentedId))
        {
            return SecurityAuditActor.Anonymous;
        }

        return new SecurityAuditActor(SecurityAuditActorKinds.Session, CallerHash: Recorder(context).CallerHashOf(SecurityAuditRecorder.SessionTenant, presentedId));
    }

    private static Task RecordAsync(HttpContext context, SecurityAuditEntry entry)
    {
        return Recorder(context).RecordAsync(entry, context.RequestAborted);
    }

    private static ISecurityAuditRecorder Recorder(HttpContext context)
    {
        return context.RequestServices.GetRequiredService<ISecurityAuditRecorder>();
    }
}
