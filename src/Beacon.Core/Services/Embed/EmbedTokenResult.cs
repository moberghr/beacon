namespace Beacon.Core.Services.Embed;

public record EmbedTokenResult(string Token, DateTimeOffset ExpiresAt, string Jti);
