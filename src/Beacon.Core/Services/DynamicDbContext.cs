using System.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Services;

internal class DynamicDbContext : DbContext
{
    private readonly string? _tableName;
    private readonly List<string>? _primaryKeys;

    public DynamicDbContext(DbContextOptions options, string? tableName = null, List<string>? primaryKeys = null) : base(options)
    {
        _tableName = tableName;
        _primaryKeys = primaryKeys;
    }

    // Add DbSet for ExpandoObject to allow BulkInsertOrUpdate operations
    public DbSet<ExpandoObject> DynamicEntities { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configure ExpandoObject as keyless entity
        // The primary keys will be specified in BulkConfig for upsert operations
        modelBuilder.Entity<ExpandoObject>().HasNoKey().ToTable(_tableName ?? "DynamicTable");

        base.OnModelCreating(modelBuilder);
    }
}