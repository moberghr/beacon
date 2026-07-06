using Beacon.Core.Data.Enums;
using Beacon.Core.Models;

namespace Beacon.Core.Adapters;

internal class AdapterFactory
{
    private readonly IEnumerable<IAdapter> _adapters;

    public AdapterFactory(IEnumerable<IAdapter> adapters)
    {
        _adapters = adapters;
    }

    public IAdapter GetAdapterService(NotificationType notificationType)
    {
        var adapter = _adapters.FirstOrDefault(e => e.NotificationType == notificationType);

        if (adapter == null)
        {
            throw new BeaconException(
                $"No adapter registered for notification type '{notificationType}'. " +
                $"Available types: {string.Join(", ", _adapters.Select(a => a.NotificationType))}");
        }

        return adapter;
    }
}