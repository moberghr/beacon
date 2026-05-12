namespace Beacon.Core.Services.Embed;

public record EmbedTokenClaims(
    int ApiKeyId,
    EmbedResource Resource,
    DateTimeOffset ExpiresAt,
    DateTimeOffset IssuedAt,
    string Jti,
    IReadOnlyDictionary<string, string> RowFilters,
    IReadOnlyList<string> AllowedOrigins,
    string Scope);
