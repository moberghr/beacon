namespace Beacon.Core.Configuration;

/// <summary>
/// How Beacon treats a data-source connection that does not authenticate the server over TLS, bound from
/// <c>Beacon:DataSources:Transport</c>. A connection is checked by its connector's
/// <see cref="Services.Providers.IDataSourceTransportInspector"/>: a new or changed connection that fails the check is
/// refused; a stored one keeps working, with one Warning per data source per process, unless
/// <see cref="EnforceOnExisting"/> is set. A host-managed source's connection string comes from the host's configuration,
/// not from an Admin, so it is never refused as a new connection: it is checked each time the source connects, as a
/// stored one (the Warning, or the refusal under <see cref="EnforceOnExisting"/>).
/// </summary>
public sealed class DataSourceTransportOptions
{
    public const string SectionName = "Beacon:DataSources:Transport";

    /// <summary>
    /// Refuses a stored data source whose connection fails the check when it connects, in place of the Warning. Default
    /// false, so sources stored before the check keep working until an Admin replaces their connection.
    /// </summary>
    public bool EnforceOnExisting { get; set; }

    /// <summary>
    /// Lifts the check altogether: new, changed and stored connections are accepted without server authentication. For
    /// development hosts only; the host logs a Warning at startup when it is set outside Development. Default false.
    /// </summary>
    public bool AllowInsecure { get; set; }
}
