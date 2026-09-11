using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Beacon.Core.Data;

/// <summary>
/// Incorporates the configured schema into the model cache key so two <see cref="BeaconContext"/>
/// instances configured with different schemas via <c>UseBeaconSchema</c> build and cache
/// distinct EF models, instead of the second schema silently reusing the first schema's cached
/// model. Shared by both the SQL Server and PostgreSQL providers — it needs no provider-specific
/// type at all, so it lives in Core (§2.4).
/// </summary>
public sealed class BeaconSchemaModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        var schema = BeaconSchema.Resolve(context);

        return (context.GetType(), designTime, schema);
    }
}
