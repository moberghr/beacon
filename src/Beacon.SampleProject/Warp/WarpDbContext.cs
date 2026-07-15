using Microsoft.EntityFrameworkCore;

namespace Beacon.SampleProject.Warp;

/// <summary>
/// Dedicated EF Core context that owns Warp's background-job schema (the <c>warp</c> schema:
/// jobs, messages, recurring jobs, servers/workers, background-service and addon tables).
///
/// Kept deliberately separate from <c>BeaconContext</c>: Warp wires its entities and row-lock
/// interceptors in by decorating <c>DbContextOptions&lt;TContext&gt;</c> and replacing
/// <c>IModelCustomizer</c> (see <c>AddWarpServer</c>). BeaconContext is abstract, built through
/// an <c>IDbContextFactory</c> adapter, dual-provider, and owns 100+ entities under strict
/// append-only migration rules — so pointing Warp at its own context isolates all of that and
/// keeps Beacon's model, migrations, and EF behaviour untouched.
///
/// Warp supplies the entire model through the replaced <c>IModelCustomizer</c>, so this class
/// needs no <c>OnModelCreating</c> of its own.
/// </summary>
public sealed class WarpDbContext(DbContextOptions<WarpDbContext> options) : DbContext(options);
