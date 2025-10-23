using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;

namespace Semantico.Core.PostgreSql.Data;

internal sealed class PostgreSqlSemanticoContext : SemanticoContext
{
    public PostgreSqlSemanticoContext(DbContextOptions<PostgreSqlSemanticoContext> options, string defaultSchema = "semantico")
        : base(options, defaultSchema)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(DefaultSchema);

        base.OnModelCreating(modelBuilder);
    }
}
