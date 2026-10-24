namespace Beacon.Core.Services.Embed;

[Obsolete("Not used by Beacon's embed routes; removed in 6.0")]
public record EmbedTokenMintRequest(
    int ApiKeyId,
    EmbedResource Resource,
    TimeSpan Ttl,
    IReadOnlyDictionary<string, string> RowFilters,
    IReadOnlyList<string> AllowedOrigins);
