namespace Beacon.Core.Services.Embed;

[Obsolete("Not used by Beacon's embed routes; removed in 6.0")]
public enum EmbedResourceType
{
    Query
}

[Obsolete("Not used by Beacon's embed routes; removed in 6.0")]
public record EmbedResource(EmbedResourceType Type, string Id);
