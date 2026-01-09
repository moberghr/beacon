using Semantico.Core.Data.Enums;
using Semantico.Core.Models;

namespace Semantico.Core.Adapters;

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
            throw new SemanticoException(
                $"No adapter registered for notification type '{notificationType}'. " +
                $"Available types: {string.Join(", ", _adapters.Select(a => a.NotificationType))}");
        }

        return adapter;
    }
}