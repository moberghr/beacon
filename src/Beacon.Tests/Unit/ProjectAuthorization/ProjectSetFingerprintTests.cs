using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// A cached value shared by callers whose project access reaches the same rows only: the fingerprint tells apart every
/// part of a set that changes what it reaches, and keeps the owner it names out of the key's text.
/// </summary>
[TestFixture]
public class ProjectSetFingerprintTests
{
    private static readonly ProjectSet Member = new([1, 2], true, [9]);

    [Test]
    public void EqualSets_ShareAFingerprint()
    {
        ProjectSetFingerprint.Of(new ProjectSet([2, 1], true, [9])).Should().Be(ProjectSetFingerprint.Of(Member));
    }

    [Test]
    public void TheOpenFastPath_AndAnUnrestrictedCaller_DoNotShareOne()
    {
        ProjectSetFingerprint.Of(ProjectSet.All).Should().NotBe(ProjectSetFingerprint.Of(ProjectSet.Unrestricted));
    }

    [Test]
    public void ACallerConfinedToItsList_AndOneReachingTheQueriesItOwns_EachHaveTheirOwn()
    {
        var fingerprints = new[]
        {
            ProjectSetFingerprint.Of(Member),
            ProjectSetFingerprint.Of(Member with { IsConfinedToList = true }),
            ProjectSetFingerprint.Of(Member with { QueryOwnerUserId = "ext-ada" }),
            ProjectSetFingerprint.Of(Member with { QueryOwnerUserId = "ext-ben" })
        };

        fingerprints.Should().OnlyHaveUniqueItems();
        fingerprints[2].Should().NotContain("ext-ada", "the owner is hashed into the key");
    }
}
