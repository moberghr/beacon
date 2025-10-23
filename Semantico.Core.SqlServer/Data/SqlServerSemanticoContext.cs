using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;

namespace Semantico.Core.SqlServer.Data;

internal partial class SqlServerSemanticoContext : SemanticoContext
{
    public SqlServerSemanticoContext(DbContextOptions<SqlServerSemanticoContext> options, string defaultSchema = "semantico")
        : base(options, defaultSchema)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(DefaultSchema);

        base.OnModelCreating(modelBuilder);
    }
}
