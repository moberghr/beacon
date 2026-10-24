using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Beacon.Core.Data;

/// <summary>
/// Incorporates the schema set with <c>UseBeaconSchema</c> into the model cache key. Beacon's schema is fixed to
/// <c>beacon</c> and Beacon's providers no longer use it; kept for hosts that registered it themselves.
/// </summary>
[Obsolete("Beacon's schema is fixed to 'beacon', so the model cache key needs no schema; Beacon no longer uses this type. It is removed in 6.0.")]
public sealed class BeaconSchemaModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        var schema = BeaconSchema.Resolve(context);

        return (context.GetType(), designTime, schema);
    }
}
