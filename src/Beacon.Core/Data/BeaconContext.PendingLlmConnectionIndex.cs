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
    // One pending LLM connection change at a time (there is one stored connection): a concurrent second submission fails on
    // this index. Filter column names differ per provider (SQL Server keeps PascalCase, PostgreSQL uses snake_case via the
    // naming convention).
    private void ConfigurePendingLlmConnectionIndex(ModelBuilder modelBuilder)
    {
        var pendingLlmConnectionFilter = Database.ProviderName == HostModelReader.SqlServerProviderName
            ? $"[Kind] = {(int)ChangeApprovalKind.LlmConnection} AND [Status] = {(int)ApprovalStatus.Pending}"
            : $"kind = {(int)ChangeApprovalKind.LlmConnection} AND status = {(int)ApprovalStatus.Pending}";
        modelBuilder.Entity<ChangeApprovalRequest>()
            .HasIndex(e => new { e.Kind, e.ResourceId })
            .IsUnique()
            .HasFilter(pendingLlmConnectionFilter);
    }
}
