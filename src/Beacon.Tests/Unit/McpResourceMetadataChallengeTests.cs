using FluentAssertions;
using NUnit.Framework;
using Beacon.MCP.Discovery;

namespace Beacon.Tests.Unit;

/// <summary>
/// Header-mutation rules for the RFC 9728 <c>resource_metadata</c> challenge parameter
/// (<see cref="McpDiscoveryEndpoints.BuildResourceMetadataChallenge"/>): a 401 from
/// <c>/beacon/mcp</c> must carry the pointer whether the response has no challenge yet OR
/// already carries the bare <c>Bearer</c> that <c>JwtBearerAuthMiddleware</c> writes on invalid
/// tokens — while a challenge that already has the parameter, or a non-Bearer scheme, stays
/// untouched.
/// </summary>
[TestFixture]
public class McpResourceMetadataChallengeTests
{
    private const string MetadataUrl = "https://beacon.example.com/.well-known/oauth-protected-resource";
    private const string Parameter = $"resource_metadata=\"{MetadataUrl}\"";

    [Test]
    public void NoExistingHeader_AddsBearerChallengeWithPointer()
    {
        McpDiscoveryEndpoints.BuildResourceMetadataChallenge(null, MetadataUrl)
            .Should().Be($"Bearer {Parameter}");
        McpDiscoveryEndpoints.BuildResourceMetadataChallenge("", MetadataUrl)
            .Should().Be($"Bearer {Parameter}");
    }

    [Test]
    public void BareBearer_AppendsPointerParameter()
    {
        // JwtBearerAuthMiddleware writes exactly "Bearer" on invalid tokens — the pointer must be
        // appended, not skipped, or remote clients cannot bootstrap OAuth discovery from that 401.
        McpDiscoveryEndpoints.BuildResourceMetadataChallenge("Bearer", MetadataUrl)
            .Should().Be($"Bearer {Parameter}");
    }

    [Test]
    public void BearerWithExistingParameters_AppendsPointerPreservingThem()
    {
        McpDiscoveryEndpoints.BuildResourceMetadataChallenge("Bearer error=\"invalid_token\"", MetadataUrl)
            .Should().Be($"Bearer error=\"invalid_token\", {Parameter}");
    }

    [Test]
    public void HeaderAlreadyCarryingResourceMetadata_IsUntouched()
    {
        McpDiscoveryEndpoints.BuildResourceMetadataChallenge($"Bearer {Parameter}", MetadataUrl)
            .Should().BeNull("a challenge that already carries the pointer must not be rewritten");
    }

    [Test]
    public void NonBearerScheme_IsUntouched()
    {
        McpDiscoveryEndpoints.BuildResourceMetadataChallenge("Basic realm=\"beacon\"", MetadataUrl)
            .Should().BeNull("resource_metadata is a Bearer auth-param (RFC 9728 §5.1)");
        McpDiscoveryEndpoints.BuildResourceMetadataChallenge("BearerX custom", MetadataUrl)
            .Should().BeNull("a scheme merely PREFIXED with 'Bearer' is not the Bearer scheme");
    }
}
