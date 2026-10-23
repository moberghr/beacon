namespace Beacon.Core.Services.Embed;

public record EmbedTokenResult(string Token, DateTimeOffset ExpiresAt, string Jti)
{
    // The compiler-generated ToString prints every member; the secrets are printed redacted.
    protected virtual bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Token = {RedactedPrint.Of(Token)}, ExpiresAt = {ExpiresAt}, Jti = {Jti}");

        return true;
    }
}
