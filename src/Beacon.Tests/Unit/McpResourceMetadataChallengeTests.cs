using FluentAssertions;
using NUnit.Framework;
using Beacon.MCP.Discovery;

namespace Beacon.Tests.Unit;

/// <summary>
/// Header-mutation rules for the RFC 9728 <c>resource_metadata</c> challenge parameter
/// (<see cref="McpDiscoveryEndpoints.BuildResourceMetadataChallenge"/>): a 401 from
/// <c>/beacon/mcp</c> must carry the pointer whether the response has no challenge yet, already
/// carries the bare <c>Bearer</c> that <c>JwtBearerAuthMiddleware</c> writes on invalid tokens,
/// or carries only foreign schemes (R6-5: a separate Bearer challenge is appended, never a
/// mutation of the foreign one). Only a Bearer challenge that already has the parameter — an
/// exact auth-param NAME match, never a substring — leaves the header untouched.
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
    public void NonBearerScheme_GetsSeparateBearerChallengeAppended()
    {
        // R6-5: resource_metadata is a Bearer auth-param (RFC 9728 §5.1) — a foreign scheme is
        // never mutated; a SEPARATE Bearer challenge carrying the pointer is appended instead.
        McpDiscoveryEndpoints.BuildResourceMetadataChallenge("Basic realm=\"beacon\"", MetadataUrl)
            .Should().Be($"Basic realm=\"beacon\", Bearer {Parameter}");
        McpDiscoveryEndpoints.BuildResourceMetadataChallenge("BearerX custom", MetadataUrl)
            .Should().Be($"BearerX custom, Bearer {Parameter}",
                "a scheme merely PREFIXED with 'Bearer' is not the Bearer scheme");
    }

    [Test]
    public void MultiSchemeHeader_AppendsParameterToTheBearerChallenge()
    {
        McpDiscoveryEndpoints.BuildResourceMetadataChallenge(
                "Negotiate, Bearer error=\"invalid_token\"", MetadataUrl)
            .Should().Be($"Negotiate, Bearer error=\"invalid_token\", {Parameter}",
                "the parameter belongs to the Bearer challenge, not the Negotiate one");
    }

    [Test]
    public void MultiSchemeHeader_BearerAlreadyCarryingPointer_IsUntouched()
    {
        McpDiscoveryEndpoints.BuildResourceMetadataChallenge(
                $"Negotiate, Bearer {Parameter}", MetadataUrl)
            .Should().BeNull("the Bearer challenge already carries the pointer");
    }

    [Test]
    public void NonBearerParamContainingResourceMetadataSubstring_StillAppendsBearerChallenge()
    {
        // The presence check is an exact Bearer auth-param NAME match — a foreign challenge whose
        // param merely CONTAINS the text must not suppress the pointer.
        McpDiscoveryEndpoints.BuildResourceMetadataChallenge(
                "Custom error=\"resource_metadata_missing\"", MetadataUrl)
            .Should().Be($"Custom error=\"resource_metadata_missing\", Bearer {Parameter}");
    }

    [Test]
    public void BearerParamValueContainingResourceMetadataText_StillGetsThePointerAppended()
    {
        McpDiscoveryEndpoints.BuildResourceMetadataChallenge(
                "Bearer error=\"resource_metadata_hint\"", MetadataUrl)
            .Should().Be($"Bearer error=\"resource_metadata_hint\", {Parameter}",
                "only a resource_metadata auth-param NAME counts as the pointer being present");
    }
}
