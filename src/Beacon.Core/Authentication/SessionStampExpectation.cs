namespace Beacon.Core.Authentication;

/// <summary>
/// The security stamp a password of this request was verified with: the user's external id, and the stamp read in the
/// same query as the password hash (or the one written with the user's new password). The cookie sign-in that follows
/// refuses the session when the user's stored stamp is no longer that one, so a sign-in never adopts a stamp newer than
/// the credentials it checked. One per request (scoped); a sign-in without a password verified on its request (single
/// sign-on) has no expectation and carries the stored stamp.
/// </summary>
internal sealed class SessionStampExpectation
{
    private string? _externalId;
    private string _stamp = string.Empty;

    /// <summary>
    /// Records that the password of the user <paramref name="externalId"/> names was verified (or set) with
    /// <paramref name="stamp"/>; a user without a stamp has "". Replaces an earlier record of the request.
    /// </summary>
    public void Record(string? externalId, string? stamp)
    {
        _externalId = string.IsNullOrEmpty(externalId) ? null : externalId;
        _stamp = stamp ?? string.Empty;
    }

    /// <summary>
    /// The stamp a sign-in of <paramref name="externalId"/> must find stored ("" for none), or null when no password of
    /// that user was verified on this request.
    /// </summary>
    public string? StampFor(string externalId)
    {
        return _externalId != null && string.Equals(_externalId, externalId, StringComparison.Ordinal)
            ? _stamp
            : null;
    }
}
