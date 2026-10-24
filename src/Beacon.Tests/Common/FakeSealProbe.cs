using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Beacon.Core.SecurityAudit;

namespace Beacon.Tests.Common;

/// <summary>A catalog read that answers whether the sealed audit tables lack their seal columns, and counts its reads.</summary>
internal sealed class FakeSealProbe(bool? missing) : IAuditTableSealProbe
{
    public bool? Missing { get; set; } = missing;

    public int Reads { get; private set; }

    /// <summary>The schema over a probe answering <paramref name="missing"/>.</summary>
    public static AuditTableSealSchema Schema(bool? missing, TimeProvider? time = null, ILoggerFactory? loggerFactory = null)
    {
        return new AuditTableSealSchema(new FakeSealProbe(missing), time ?? TimeProvider.System, loggerFactory ?? NullLoggerFactory.Instance);
    }

    public bool? ColumnsMissing(DbContext context, IEntityType entity)
    {
        Reads++;

        return Missing;
    }

    public Task<bool?> ColumnsMissingAsync(DbContext context, IEntityType entity, CancellationToken cancellationToken)
    {
        Reads++;

        return Task.FromResult(Missing);
    }
}
