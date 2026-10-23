using Beacon.Core.HostData;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using NUnit.Framework;

namespace Beacon.Tests.Unit.HostData;

[TestFixture]
public class HostSecretColumnTests
{
    [Test]
    public void Read_AllowAllTables_HardExcludesCredentialColumnsAndCredentialTables()
    {
        var snapshot = Read(x => x.AllowAllTables());

        Excluded(snapshot, "AspNetUsers").Should().BeEquivalentTo("SecurityStamp", "PasswordHash", "TotpKey", "RecoveryCodes", "ConnectionString");
        Visible(snapshot, "AspNetUsers").Should().BeEquivalentTo("Id", "UserName", "ConcurrencyStamp", "SigningDate", "CertificateNumber", "RecoveredAmount");
        Excluded(snapshot, "AspNetUserTokens").Should().BeEquivalentTo("UserId", "LoginProvider", "Name", "Value");
        Excluded(snapshot, "ApiKeys").Should().BeEquivalentTo("Id", "Label");
        Excluded(snapshot, "HostKeyRing").Should().BeEquivalentTo("Id", "FriendlyName", "Xml");
        Excluded(snapshot, "Cards").Should().BeEquivalentTo("Cvv", "EncryptionKey");
        Visible(snapshot, "Cards").Should().BeEquivalentTo("Id", "ProductKey");
    }

    [Test]
    public void Read_IncludeSecretLikeColumn_LiftsACredentialTableColumn()
    {
        var snapshot = Read(x => x
            .AllowTables("ApiKeys")
            .IncludeSecretLikeColumn("ApiKeys.Label"));

        Excluded(snapshot, "ApiKeys").Should().BeEquivalentTo("Id");
        Visible(snapshot, "ApiKeys").Should().BeEquivalentTo("Label");
    }

    [TestCase("SecurityStamp", true)]
    [TestCase("security_stamp", true)]
    [TestCase("ConnectionString", true)]
    [TestCase("client_credential", true)]
    [TestCase("Passphrase", true)]
    [TestCase("Pwd", true)]
    [TestCase("TotpKey", true)]
    [TestCase("MfaSecret", true)]
    [TestCase("mfa_enabled", true)]
    [TestCase("RecoveryCodes", true)]
    [TestCase("Cvv", true)]
    [TestCase("card_cvc", true)]
    [TestCase("CertificatePfx", true)]
    [TestCase("Nonce", true)]
    [TestCase("WalletSeed", true)]
    [TestCase("SigningKey", true)]
    [TestCase("encryption_key", true)]
    [TestCase("AuthenticatorKey", true)]
    [TestCase("TimeStamp", false)]
    [TestCase("ConcurrencyStamp", false)]
    [TestCase("SigningDate", false)]
    [TestCase("CertificateNumber", false)]
    [TestCase("RecoveredAmount", false)]
    [TestCase("ProductKey", false)]
    [TestCase("session_id", false)]
    [TestCase("request_signature", false)]
    public void IsSecretLike_MatchesCredentialNames_NotBusinessWords(string name, bool expected)
    {
        SecretLikeColumnNames.IsSecretLike(name).Should().Be(expected);
    }

    [TestCase("AspNetUserTokens", true)]
    [TestCase("DataProtectionKeys", true)]
    [TestCase("signing_keys", true)]
    [TestCase("RefreshTokens", true)]
    [TestCase("Keys", true)]
    [TestCase("Customer", false)]
    [TestCase("KeyAccounts", false)]
    [TestCase("TokenUsage", false)]
    [TestCase(null, false)]
    public void IsSecretLikeTable_MatchesTablesNamedForKeysOrTokens(string? table, bool expected)
    {
        SecretLikeColumnNames.IsSecretLikeTable(table).Should().Be(expected);
    }

    private static HostExposureSnapshot Read(Action<HostDbContextOptions> configure)
    {
        using var context = new HostSecretsContext(new DbContextOptionsBuilder<HostSecretsContext>()
            .UseSqlServer("Server=unused;Database=unused")
            .Options);
        var options = new HostDbContextOptions { ReadOnlyConnectionStringName = "BeaconReadOnly" };
        configure(options);

        return HostModelReader.Read(
            context.GetService<IDesignTimeModel>().Model,
            HostModelReader.InferEngine(context.Database.ProviderName, null),
            new HostDataSourceRegistration(typeof(HostSecretsContext), options),
            new FakeXmlDocumentation());
    }

    private static IEnumerable<string> Excluded(HostExposureSnapshot snapshot, string table)
    {
        return snapshot.Policy.Tables
            .Where(x => x.Name == table)
            .Single()
            .ExcludedColumns;
    }

    private static IEnumerable<string> Visible(HostExposureSnapshot snapshot, string table)
    {
        return snapshot.Tables
            .Where(x => x.TableName == table)
            .Single()
            .Columns
            .Select(x => x.ColumnName);
    }
}

public sealed class HostAppUser
{
    public int Id { get; set; }
    public string UserName { get; set; } = "";
    public string SecurityStamp { get; set; } = "";
    public string ConcurrencyStamp { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string TotpKey { get; set; } = "";
    public string RecoveryCodes { get; set; } = "";
    public string ConnectionString { get; set; } = "";
    public DateTime SigningDate { get; set; }
    public string CertificateNumber { get; set; } = "";
    public decimal RecoveredAmount { get; set; }
}

public sealed class HostAppUserToken
{
    public int UserId { get; set; }
    public string LoginProvider { get; set; } = "";
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class HostApiKey
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

public sealed class HostCard
{
    public int Id { get; set; }
    public string Cvv { get; set; } = "";
    public string EncryptionKey { get; set; } = "";
    public string ProductKey { get; set; } = "";
}

public sealed class HostSecretsContext(DbContextOptions<HostSecretsContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HostAppUser>().ToTable("AspNetUsers");
        modelBuilder.Entity<HostAppUserToken>(entity =>
        {
            entity.ToTable("AspNetUserTokens");
            entity.HasKey(x => new { x.UserId, x.LoginProvider, x.Name });
        });
        modelBuilder.Entity<HostApiKey>().ToTable("ApiKeys");
        modelBuilder.Entity<HostCard>().ToTable("Cards");

        // The Data Protection key entity under a table name no rule knows: its type alone hides every column.
        modelBuilder.Entity<DataProtectionKey>().ToTable("HostKeyRing");
    }
}
