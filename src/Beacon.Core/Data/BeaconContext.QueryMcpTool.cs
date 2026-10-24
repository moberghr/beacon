using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Entities.RowSecurity;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using System.Linq.Expressions;

namespace Beacon.Core.Data;

public abstract partial class BeaconContext
{
    protected void ConfigureQueryMcpToolEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Query>(entity =>
        {
            entity.Property(e => e.McpToolName).HasMaxLength(64);
            entity.Property(e => e.McpToolDescription).HasMaxLength(1000);

            // A tool name means one query for every caller, whichever of their projects they reach it through:
            // unique across live queries. Filter column names differ per provider (SQL Server keeps PascalCase,
            // PostgreSQL uses snake_case via the naming convention).
            var mcpToolNameFilter = Database.ProviderName == HostModelReader.SqlServerProviderName
                ? "[McpToolName] IS NOT NULL AND [ArchivedTime] IS NULL"
                : "mcp_tool_name IS NOT NULL AND archived_time IS NULL";
            entity.HasIndex(e => e.McpToolName)
                .IsUnique()
                .HasFilter(mcpToolNameFilter);
        });
    }
}
