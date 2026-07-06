namespace Beacon.Core.Services.Embed;

public enum EmbedResourceType
{
    Query,
    Dashboard
}

public record EmbedResource(EmbedResourceType Type, string Id);
