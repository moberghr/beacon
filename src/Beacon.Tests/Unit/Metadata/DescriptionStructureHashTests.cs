using System.Globalization;
using Beacon.Core.Services.Metadata;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Metadata;

/// <summary>
/// The structure hash a description item records: SHA-256 hex of the table's column names and data types in ordinal order,
/// each written with its length in front, the same on every culture.
/// </summary>
[TestFixture]
public class DescriptionStructureHashTests
{
    // SHA-256 of "2:id7:integer6:status4:text".
    private const string OrdersHash = "b64cea7567e48b5830903987d4d1c1b765c14be573efdda112357b6cdeb34a54";

    [Test]
    public void TheHash_IsPinned()
    {
        DescriptionStructureHash.Of([("id", "integer"), ("status", "text")]).Should().Be(OrdersHash);
    }

    [Test]
    public void ATableWithoutColumns_HashesTheEmptyStructure()
    {
        DescriptionStructureHash.Of(Array.Empty<(string, string)>()).Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Test]
    public void TheOrderOfTheColumns_Matters()
    {
        DescriptionStructureHash.Of([("status", "text"), ("id", "integer")]).Should().NotBe(OrdersHash);
    }

    [Test]
    public void AChangedDataType_ChangesTheHash()
    {
        DescriptionStructureHash.Of([("id", "bigint"), ("status", "text")]).Should().NotBe(OrdersHash);
    }

    [Test]
    public void ARenamedColumn_ChangesTheHash_AndNamesCompareAsWritten()
    {
        DescriptionStructureHash.Of([("id", "integer"), ("state", "text")]).Should().NotBe(OrdersHash);
        DescriptionStructureHash.Of([("ID", "integer"), ("status", "text")]).Should().NotBe(OrdersHash);
    }

    // Without the lengths "ab" + "c" and "a" + "bc" would read the same.
    [Test]
    public void ANameOrTypeIsNeverReadAsPartOfAnother()
    {
        DescriptionStructureHash.Of([("ab", "c")]).Should().NotBe(DescriptionStructureHash.Of([("a", "bc")]));
        DescriptionStructureHash.Of([("a", "b"), ("c", "d")]).Should().NotBe(DescriptionStructureHash.Of([("a", "b2:c1:d")]));
    }

    [Test]
    public void ColumnsWithPositions_AreHashedInOrdinalOrder()
    {
        DescriptionStructureHash.Of([("status", "text", 2), ("id", "integer", 1)]).Should().Be(OrdersHash);
        DescriptionStructureHash.Of([("status", "text", 1), ("id", "integer", 2)]).Should().NotBe(OrdersHash);
    }

    // Turkish cases I and i differently, and a culture comparison would sort is_paid before ITEM_ID: the hash is the one taken
    // under the invariant culture, SHA-256 of "2:Id7:INTEGER7:ITEM_ID7:integer7:is_paid7:boolean" (ties on position in
    // ordinal order of the name).
    [Test]
    [SetCulture("tr-TR")]
    public void TheHash_IsTheSameOnAnotherCulture()
    {
        CultureInfo.CurrentCulture.Name.Should().Be("tr-TR");

        DescriptionStructureHash.Of([("is_paid", "boolean", 2), ("Id", "INTEGER", 1), ("ITEM_ID", "integer", 2)])
            .Should().Be("5dd5bd7ffe6baa238fab613cefe4eb02415b9091bf5f12ccc88917d48883afda");
    }
}
