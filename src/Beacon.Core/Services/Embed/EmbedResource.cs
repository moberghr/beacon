namespace Beacon.Core.Services.Embed;

public enum EmbedResourceType
{
    Query
}

public record EmbedResource(EmbedResourceType Type, string Id);
