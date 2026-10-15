using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;

namespace Beacon.Api;

/// <summary>
/// Warns once at startup, outside Development, when <c>Beacon:DataSources:Transport:AllowInsecure</c> is set: data-source
/// connections are then accepted without authenticating the server over TLS, so an on-path attacker can read the
/// credentials and the rows. Configuration names only.
/// </summary>
internal sealed class DataSourceTransportStartupWarning(
    IOptions<DataSourceTransportOptions> options,
    IHostEnvironment environment,
    ILogger<DataSourceTransportStartupWarning> logger) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        if (options.Value.AllowInsecure && !environment.IsDevelopment())
        {
            logger.LogWarning(
                "{Section}:{Option} is on outside Development: data-source connections are accepted without authenticating the server over TLS. Turn it off and give each data source a connection that verifies the server's certificate",
                DataSourceTransportOptions.SectionName,
                nameof(DataSourceTransportOptions.AllowInsecure));
        }

        return next;
    }
}
