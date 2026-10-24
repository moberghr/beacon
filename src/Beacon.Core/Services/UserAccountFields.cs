using System.Net.Mail;

namespace Beacon.Core.Services;

/// <summary>
/// The user name and e-mail address an administrator or first-run setup gives an account. The handlers trim what they
/// are sent (<see cref="Trimmed"/>, <see cref="TrimmedEmail"/>); <see cref="Problem"/> then names what is still invalid.
/// Accounts provisioned at an identity provider sign-in take the provider's values and are not checked here.
/// </summary>
internal static class UserAccountFields
{
    internal const int MinUserNameLength = 3;

    // The Users columns' lengths.
    internal const int MaxUserNameLength = 100;
    internal const int MaxEmailLength = 255;

    /// <summary>The user name without surrounding whitespace (empty for none).</summary>
    public static string Trimmed(string? userName)
    {
        return userName?.Trim() ?? string.Empty;
    }

    /// <summary>The e-mail address without surrounding whitespace; null for none or a blank one.</summary>
    public static string? TrimmedEmail(string? email)
    {
        return string.IsNullOrWhiteSpace(email) ? null : email.Trim();
    }

    /// <summary>
    /// Why <paramref name="userName"/> or <paramref name="email"/> cannot be stored, or null when both can: a user name of
    /// 3 to 100 characters with no surrounding whitespace and no control characters; no e-mail address (null or empty), or
    /// one plain address of at most 255 characters (no display name, no list, no surrounding whitespace).
    /// </summary>
    public static string? Problem(string? userName, string? email)
    {
        return UserNameProblem(userName) ?? EmailProblem(email);
    }

    public static string? UserNameProblem(string? userName)
    {
        if (string.IsNullOrEmpty(userName) || userName != userName.Trim())
        {
            return "A user name is required and must not start or end with whitespace.";
        }

        if (userName.Length is < MinUserNameLength or > MaxUserNameLength)
        {
            return $"The user name must be {MinUserNameLength} to {MaxUserNameLength} characters long.";
        }

        return userName.Any(char.IsControl)
            ? "The user name must not contain control characters."
            : null;
    }

    public static string? EmailProblem(string? email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return null;
        }

        var plainAddress = email.Length <= MaxEmailLength
            && MailAddress.TryCreate(email, out var address)
            && string.Equals(address.Address, email, StringComparison.Ordinal)
            && string.IsNullOrEmpty(address.DisplayName);

        return plainAddress
            ? null
            : $"The e-mail address must be a single plain address of at most {MaxEmailLength} characters.";
    }
}
