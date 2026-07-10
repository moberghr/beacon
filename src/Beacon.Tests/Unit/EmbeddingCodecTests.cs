using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Helpers;

namespace Beacon.Tests.Unit;

[TestFixture]
public class EmbeddingCodecTests
{
    [Test]
    public void ToBytes_FromBytes_RoundTripsExactly()
    {
        var original = new[] { 0f, 1f, -1f, 3.14159f, float.MaxValue, float.MinValue, 1.0e-30f };

        var bytes = EmbeddingCodec.ToBytes(original);
        var restored = EmbeddingCodec.FromBytes(bytes);

        bytes.Length.Should().Be(original.Length * sizeof(float));
        restored.Should().Equal(original);
    }

    [Test]
    public void FromBytes_LengthNotMultipleOfFour_Throws()
    {
        var act = () => EmbeddingCodec.FromBytes([1, 2, 3]);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Cosine_IdenticalVectors_IsApproximatelyOne()
    {
        var vector = new[] { 1f, 2f, 3f, 4f };

        EmbeddingCodec.Cosine(vector, vector).Should().BeApproximately(1.0, 1e-6);
    }

    [Test]
    public void Cosine_OrthogonalVectors_IsApproximatelyZero()
    {
        var a = new[] { 1f, 0f };
        var b = new[] { 0f, 1f };

        EmbeddingCodec.Cosine(a, b).Should().BeApproximately(0.0, 1e-6);
    }

    [Test]
    public void Cosine_ZeroNormVector_IsZero()
    {
        var zero = new[] { 0f, 0f, 0f };
        var other = new[] { 1f, 2f, 3f };

        EmbeddingCodec.Cosine(zero, other).Should().Be(0);
    }

    [Test]
    public void Cosine_MismatchedLengths_Throws()
    {
        var act = () => EmbeddingCodec.Cosine([1f, 2f], [1f, 2f, 3f]);

        act.Should().Throw<ArgumentException>();
    }
}
