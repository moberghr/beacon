using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.RowSecurity;

namespace Beacon.Tests.Common;

/// <summary>Row-level security as a host that leaves <c>Beacon:RowSecurity</c> unset gets it: the lock off.</summary>
internal static class TestRowSecurity
{
    /// <summary>The context every statement gets while the lock is off: no row filter.</summary>
    public static RowSecurityContext NotApplicable { get; } = RowSecurityContext.NotApplicable(RowSecurityContext.DisabledReason);

    /// <summary>The resolver with the lock off.</summary>
    public static IRowSecurityResolver Resolver { get; } = new RowSecurityResolver(Options.Create(new RowSecurityOptions()));
}
