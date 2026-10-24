namespace Beacon.Core.Services.Embed;

[Obsolete("Not used by Beacon's embed routes; removed in 6.0")]
public record EmbedTokenResult(string Token, DateTimeOffset ExpiresAt, string Jti);
