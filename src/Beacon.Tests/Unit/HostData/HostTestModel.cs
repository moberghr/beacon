using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Beacon.Tests.Unit.HostData;

/// <summary>A small host model (loans domain) used to exercise ExposeDbContext without a database.</summary>
public sealed class HostCustomer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string SSN { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Secret { get; set; } = "";
    public int TokenCount { get; set; }
    public string InternalNote { get; set; } = "";
    public HostAddress Address { get; set; } = new();
    public List<HostLoan> Loans { get; set; } = [];
}

public sealed class HostAddress
{
    public string Street { get; set; } = "";
}

public sealed class HostLoan
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public HostCustomer Customer { get; set; } = null!;
    public decimal Amount { get; set; }
    public string AccountNumber { get; set; } = "";
}

public sealed class HostAuditLog
{
    public int Id { get; set; }
    public string Payload { get; set; } = "";
}

public sealed class HostLoanSummary
{
    public int CustomerId { get; set; }
    public decimal Total { get; set; }
}

public sealed class HostTestContext(DbContextOptions<HostTestContext> options) : DbContext(options)
{
    public DbSet<HostCustomer> Customers => Set<HostCustomer>();
    public DbSet<HostLoan> Loans => Set<HostLoan>();
    public DbSet<HostAuditLog> AuditLogs => Set<HostAuditLog>();
    public DbSet<HostLoanSummary> LoanSummaries => Set<HostLoanSummary>();

    public static HostTestContext SqlServer()
    {
        return new HostTestContext(new DbContextOptionsBuilder<HostTestContext>()
            .UseSqlServer("Server=unused;Database=unused")
            .Options);
    }

    public static HostTestContext Npgsql()
    {
        return new HostTestContext(new DbContextOptionsBuilder<HostTestContext>()
            .UseNpgsql("Host=unused;Database=unused")
            .Options);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HostCustomer>(entity =>
        {
            entity.ToTable("Customer", x => x.HasComment("Customers of the bank"));
            entity.Property(x => x.SSN).HasMaxLength(10).HasComment("Kennitala");
            entity.OwnsOne(x => x.Address);
            entity.HasIndex(x => x.Name);
        });

        modelBuilder.Entity<HostLoan>(entity =>
        {
            entity.ToTable("Loan", "lending");
            entity.HasOne(x => x.Customer)
                .WithMany(x => x.Loans)
                .HasForeignKey(x => x.CustomerId);
        });

        modelBuilder.Entity<HostAuditLog>().ToTable("AuditLog");

        modelBuilder.Entity<HostLoanSummary>(entity =>
        {
            entity.HasNoKey();
            entity.ToView("LoanSummary");
        });
    }
}

internal sealed class FakeXmlDocumentation(Dictionary<string, string>? members = null) : IXmlDocumentationProvider
{
    private readonly Dictionary<string, string> _members = members ?? [];

    public string? GetTypeSummary(Type type)
    {
        return _members.GetValueOrDefault($"T:{type.Name}");
    }

    public string? GetPropertySummary(Type declaringType, string propertyName)
    {
        return _members.GetValueOrDefault($"P:{declaringType.Name}.{propertyName}");
    }
}

internal static class HostTestModelFactory
{
    public static HostDataSourceRegistration Registration(Action<HostDbContextOptions> configure)
    {
        var options = new HostDbContextOptions { ReadOnlyConnectionStringName = "BeaconReadOnly" };
        configure(options);

        return new HostDataSourceRegistration(typeof(HostTestContext), options);
    }

    public static HostExposureSnapshot Read(
        Action<HostDbContextOptions> configure,
        bool npgsql = false,
        IXmlDocumentationProvider? documentation = null)
    {
        using var context = npgsql ? HostTestContext.Npgsql() : HostTestContext.SqlServer();
        var registration = Registration(configure);
        var engine = HostModelReader.InferEngine(context.Database.ProviderName, null);

        return HostModelReader.Read(
            context.GetService<IDesignTimeModel>().Model,
            engine,
            registration,
            documentation ?? new FakeXmlDocumentation());
    }

    /// <summary>The standard pilot-style exposure: Customer + Loan allow-listed, SSN and AccountNumber masked.</summary>
    public static HostExposurePolicy StandardPolicy(bool npgsql = false)
    {
        return Read(
                x => x
                    .AllowTables("Customer", "lending.Loan")
                    .ExcludeColumns(y => y.Name == "InternalNote")
                    .MaskColumns(y => y.Name is "SSN" or "AccountNumber"),
                npgsql)
            .Policy;
    }

    public static DatabaseEngineType EngineOf(bool npgsql) => npgsql ? DatabaseEngineType.PostgreSQL : DatabaseEngineType.MSSQL;
}
