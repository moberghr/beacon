using Beacon.Core.HostData;

namespace Beacon.Tests.Common;

/// <summary>The host data sources a test host declares (none by default), without building their snapshots.</summary>
internal sealed class TestHostDataSources(params HostDataSourceRegistration[] registrations) : IHostDataSourceRegistry
{
    public IReadOnlyList<HostDataSourceRegistration> Registrations { get; } = registrations;

    public HostExposureSnapshot? GetSnapshot(string hostManagedKey) => null;
}
