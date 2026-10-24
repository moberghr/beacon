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
    // Who created and who last changed an attributed resource: Users.ExternalId values, as long as an owner id.
    protected static void ConfigureAttributionColumns(ModelBuilder modelBuilder)
    {
        var attributedEntities = typeof(IAttributedEntity).Assembly.GetTypes()
            .Where(x => typeof(IAttributedEntity).IsAssignableFrom(x))
            .Where(x => x.IsClass)
            .Where(x => !x.IsAbstract);

        foreach (var attributedEntity in attributedEntities)
        {
            var entity = modelBuilder.Entity(attributedEntity);
            entity.Property(nameof(IAttributedEntity.CreatedByUserId)).HasMaxLength(200);
            entity.Property(nameof(IAttributedEntity.ModifiedByUserId)).HasMaxLength(200);
        }
    }
}
