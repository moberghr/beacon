using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.HostData;

[TestFixture]
public class HostModelReaderTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void Read_NoAllowList_ExposesNothing(bool npgsql)
    {
        var snapshot = HostTestModelFactory.Read(x => { }, npgsql);

        snapshot.Tables.Should().BeEmpty();
        snapshot.Policy.Tables.Should().BeEmpty();
        snapshot.Engine.Should().Be(HostTestModelFactory.EngineOf(npgsql));
    }

    [Test]
    public void Read_AllowTables_ExposesOnlyAllowListedTables()
    {
        var snapshot = HostTestModelFactory.Read(x => x.AllowTables("Customer", "lending.Loan"));

        snapshot.Tables
            .Select(x => $"{x.SchemaName}.{x.TableName}")
            .Should()
            .BeEquivalentTo("dbo.Customer", "lending.Loan");
    }

    [Test]
    public void Read_AllowEntity_ExposesTheMappedTable()
    {
        var snapshot = HostTestModelFactory.Read(x => x.AllowEntity<HostLoan>());

        snapshot.Tables.Should().ContainSingle(x => x.TableName == "Loan" && x.SchemaName == "lending");
    }

    [Test]
    public void Read_AllowAllTables_IncludesKeylessView()
    {
        var snapshot = HostTestModelFactory.Read(x => x.AllowAllTables());

        snapshot.Tables
            .Select(x => x.TableName)
            .Should()
            .BeEquivalentTo("Customer", "Loan", "AuditLog", "LoanSummary");
        snapshot.Tables
            .Where(x => x.TableName == "LoanSummary")
            .SelectMany(x => x.Columns)
            .Select(x => x.ColumnName)
            .Should()
            .BeEquivalentTo("CustomerId", "Total");
    }

    [Test]
    public void Read_UnknownAllowListEntry_Throws()
    {
        var act = () => HostTestModelFactory.Read(x => x.AllowTables("Customer", "Nope"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Nope*");
    }

    [Test]
    public void Read_SecretLikeColumns_AreHardExcludedFromMetadataAndPolicy()
    {
        var snapshot = HostTestModelFactory.Read(x => x.AllowTables("Customer"));
        var customer = snapshot.Tables.Single(x => x.TableName == "Customer");
        var policy = snapshot.Policy.Tables.Single();

        customer.Columns
            .Select(x => x.ColumnName)
            .Should()
            .NotContain(["PasswordHash", "Secret", "TokenCount"])
            .And.Contain(["Id", "Name", "SSN", "Address_Street"]);
        policy.ExcludedColumns.Should().BeEquivalentTo("PasswordHash", "Secret", "TokenCount");
    }

    [Test]
    public void Read_IncludeSecretLikeColumn_LiftsOnlyThatColumn()
    {
        var snapshot = HostTestModelFactory.Read(x => x
            .AllowTables("Customer")
            .IncludeSecretLikeColumn("Customer.TokenCount"));

        snapshot.Policy.Tables.Single().ExcludedColumns.Should().BeEquivalentTo("PasswordHash", "Secret");
        snapshot.Tables.Single().Columns.Should().Contain(x => x.ColumnName == "TokenCount");
    }

    [Test]
    public void Read_ExcludeAndMaskPredicates_PartitionColumns()
    {
        var snapshot = HostTestModelFactory.Read(x => x
            .AllowTables("Customer")
            .ExcludeColumns(y => y.Name == "InternalNote")
            .MaskColumns(y => y.Name is "SSN" or "InternalNote"));

        var customer = snapshot.Tables.Single();
        var policy = snapshot.Policy.Tables.Single();

        customer.Columns.Should().NotContain(x => x.ColumnName == "InternalNote");
        policy.ExcludedColumns.Should().Contain("InternalNote");
        policy.MaskedColumns.Should().BeEquivalentTo("SSN"); // exclusion wins over masking
        customer.Columns.Single(x => x.ColumnName == "SSN").Description
            .Should().Be($"Kennitala {HostModelReader.MaskedDescriptionMarker}");
    }

    [Test]
    public void Read_HostColumn_CarriesTableTypeAndEntity()
    {
        var seen = new List<HostColumn>();
        HostTestModelFactory.Read(x => x
            .AllowTables("lending.Loan")
            .ExcludeColumns(y =>
            {
                seen.Add(y);
                return false;
            }));

        seen.Should().Contain(x => x.Schema == "lending"
            && x.Table == "Loan"
            && x.Name == "Amount"
            && x.ClrType == typeof(decimal)
            && x.EntityType == typeof(HostLoan)
            && x.PropertyName == "Amount");
    }

    [TestCase(false, "dbo")]
    [TestCase(true, "public")]
    public void Read_ForeignKeys_PointAtExposedPrincipal(bool npgsql, string principalSchema)
    {
        var snapshot = HostTestModelFactory.Read(x => x.AllowTables("Customer", "lending.Loan"), npgsql);
        var customerId = snapshot.Tables
            .Single(x => x.TableName == "Loan")
            .Columns
            .Single(x => x.ColumnName == "CustomerId");

        customerId.IsForeignKey.Should().BeTrue();
        customerId.ForeignKeyTable.Should().Be("Customer");
        customerId.ForeignKeyColumn.Should().Be("Id");
        customerId.ForeignKeySchema.Should().Be(principalSchema);
        customerId.ForeignKeyConstraintName.Should().Be("FK_Loan_Customer_CustomerId");
    }

    [Test]
    public void Read_ForeignKeyToHiddenTable_IsOmitted()
    {
        var snapshot = HostTestModelFactory.Read(x => x.AllowTables("lending.Loan"));
        var customerId = snapshot.Tables.Single().Columns.Single(x => x.ColumnName == "CustomerId");

        customerId.IsForeignKey.Should().BeFalse();
        customerId.ForeignKeyTable.Should().BeNull();
    }

    [Test]
    public void Read_KeysTypesAndComments_ComeFromTheModel()
    {
        var snapshot = HostTestModelFactory.Read(x => x.AllowTables("Customer"));
        var customer = snapshot.Tables.Single();

        customer.Description.Should().Be("Customers of the bank");
        customer.Columns.Single(x => x.ColumnName == "Id").IsPrimaryKey.Should().BeTrue();
        customer.Columns.Single(x => x.ColumnName == "SSN").DataType.Should().Be("nvarchar(10)");
        customer.Columns.Single(x => x.ColumnName == "SSN").MaxLength.Should().Be(10);
        customer.Columns.Single(x => x.ColumnName == "SSN").Description.Should().Be("Kennitala");
        customer.Indexes.Should().Contain(x => x.IsPrimaryKey && x.Columns.SequenceEqual(new[] { "Id" }));
        customer.Indexes.Should().Contain(x => x.IndexName == "IX_Customer_Name");
    }

    [Test]
    public void Read_XmlDocs_FillMissingDescriptions()
    {
        var docs = new FakeXmlDocumentation(new Dictionary<string, string>
        {
            ["T:HostLoan"] = "A consumer loan.",
            ["P:HostLoan.Amount"] = "Principal in ISK."
        });

        var snapshot = HostTestModelFactory.Read(x => x.AllowTables("lending.Loan"), documentation: docs);
        var loan = snapshot.Tables.Single();

        loan.Description.Should().Be("A consumer loan.");
        loan.Columns.Single(x => x.ColumnName == "Amount").Description.Should().Be("Principal in ISK.");
    }

    [Test]
    public void Read_SameModel_ProducesStableHash_AndOptionsChangeIt()
    {
        var first = HostTestModelFactory.Read(x => x.AllowTables("Customer"));
        var second = HostTestModelFactory.Read(x => x.AllowTables("Customer"));
        var masked = HostTestModelFactory.Read(x => x.AllowTables("Customer").MaskColumns(y => y.Name == "Name"));

        second.ModelHash.Should().Be(first.ModelHash);
        masked.ModelHash.Should().NotBe(first.ModelHash);
    }

    [Test]
    public void InferEngine_UnsupportedProvider_Throws()
    {
        var act = () => HostModelReader.InferEngine("Microsoft.EntityFrameworkCore.Sqlite", null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SQL Server and PostgreSQL*");
    }

    [Test]
    public void InferEngine_ConflictingOverride_Throws()
    {
        var act = () => HostModelReader.InferEngine(HostModelReader.SqlServerProviderName, DatabaseEngineType.PostgreSQL);

        act.Should().Throw<InvalidOperationException>();
    }

    [TestCase("PasswordHash", true)]
    [TestCase("pin_code", true)]
    [TestCase("PIN", true)]
    [TestCase("OtpSeed", true)]
    [TestCase("RefreshToken", true)]
    [TestCase("KvikaAuthorizationToken", true)]
    [TestCase("api_key", true)]
    [TestCase("Salt", true)]
    [TestCase("Shipping", false)]
    [TestCase("Opinion", false)]
    [TestCase("SSN", false)]
    [TestCase("Hashtag", false)]
    public void SecretLikeColumnNames_MatchesExpectedNames(string name, bool expected)
    {
        SecretLikeColumnNames.IsSecretLike(name).Should().Be(expected);
    }
}
