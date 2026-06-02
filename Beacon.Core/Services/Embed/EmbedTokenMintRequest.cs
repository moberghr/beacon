namespace Beacon.Core.Services.Embed;

public record EmbedTokenMintRequest(
    int ApiKeyId,
    EmbedResource Resource,
    TimeSpan Ttl,
    IReadOnlyDictionary<string, string> RowFilters,
    IReadOnlyList<string> AllowedOrigins);
