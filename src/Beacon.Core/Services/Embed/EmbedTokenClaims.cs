namespace Beacon.Core.Services.Embed;

[Obsolete("Not used by Beacon's embed routes; removed in 6.0")]
public record EmbedTokenClaims(
    int ApiKeyId,
    EmbedResource Resource,
    DateTimeOffset ExpiresAt,
    DateTimeOffset IssuedAt,
    string Jti,
    IReadOnlyDictionary<string, string> RowFilters,
    IReadOnlyList<string> AllowedOrigins,
    string Scope);
