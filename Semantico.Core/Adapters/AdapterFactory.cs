using Semantico.Core.Data.Enums;

namespace Semantico.Core.Adapters;

public class AdapterFactory
{
    private readonly IEnumerable<IAdapter> _adapters;

    public AdapterFactory(IEnumerable<IAdapter> adapters)
    {
        _adapters = adapters;
    }

    public IAdapter GetAdapterService(NotificationType notificationType)
    {
        return _adapters.FirstOrDefault(e => e.NotificationType == notificationType)
               ?? throw new NotSupportedException();
    }
}