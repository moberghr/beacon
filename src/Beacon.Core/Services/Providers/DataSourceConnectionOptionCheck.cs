using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// The driver-option and host part of <see cref="DataSourceTransportPolicy"/>: what the connector's
/// <see cref="IDataSourceConnectionOptionInspector"/> refuses (unless <c>AllowInsecure</c>, or for a host-credential
/// option <c>Beacon:DataSources:AllowHostCredentials</c>, lifts it), then whether every host the connection reaches is in
/// <c>Beacon:DataSources:AllowedHosts</c> when the list is set (never lifted). A type without an option inspector has no
/// option check, and its hosts cannot be told.
/// </summary>
internal sealed class DataSourceConnectionOptionCheck(
    IEnumerable<IDataSourceConnectionOptionInspector> inspectors,
    DataSourceConnectionOptions connectionOptions,
    DataSourceTransportOptions transportOptions,
    ILogger logger)
{
    private readonly List<IDataSourceConnectionOptionInspector> _inspectors = inspectors.ToList();

    /// <summary>
    /// Null when the connection is accepted; otherwise what to refuse it with or warn about. Without
    /// <paramref name="checkOptions"/> (a host-managed source: the operator's own wiring, which may sign in as the host)
    /// only the host list applies.
    /// </summary>
    public DataSourceConnectionOptionProblem? Check(DataSourceType dataSourceType, DatabaseEngineType? databaseEngineType, string connectionData, bool checkOptions = true)
    {
        var engine = dataSourceType == DataSourceType.Database ? databaseEngineType : null;
        var verdict = Inspect(dataSourceType, engine, connectionData);
        if (checkOptions && verdict is { Accepted: false } && !IsLifted(verdict))
        {
            return new DataSourceConnectionOptionProblem(
                $"sets {verdict.Option}, which Beacon does not accept",
                verdict.Unreadable,
                $"Beacon does not accept the data-source connection option {verdict.Option}. {verdict.Remedy}.");
        }

        var allowedHosts = connectionOptions.AllowedHosts;
        if (allowedHosts.Count == 0)
        {
            return null;
        }

        var hosts = verdict?.Hosts;
        if (hosts == null)
        {
            return new DataSourceConnectionOptionProblem(
                $"reaches hosts Beacon cannot tell, and {DataSourceConnectionOptions.SectionName}:{nameof(DataSourceConnectionOptions.AllowedHosts)} is set",
                false,
                $"{DataSourceConnectionOptions.SectionName}:{nameof(DataSourceConnectionOptions.AllowedHosts)} is set, and Beacon cannot tell which hosts this connection reaches. Name the server's host explicitly.");
        }

        return hosts.All(x => IsAllowed(x, allowedHosts))
            ? null
            : new DataSourceConnectionOptionProblem(
                $"reaches a host {DataSourceConnectionOptions.SectionName}:{nameof(DataSourceConnectionOptions.AllowedHosts)} does not list",
                false,
                $"The connection reaches a host that {DataSourceConnectionOptions.SectionName}:{nameof(DataSourceConnectionOptions.AllowedHosts)} does not list. Connect to a listed host, or ask the operator to add it.");
    }

    /// <summary>
    /// Whether <paramref name="host"/> matches an entry: the same name or IP address, or a name below a <c>*.</c> entry's
    /// domain. Case-insensitive; a trailing dot and IPv6 brackets are ignored, IP addresses are compared in their canonical
    /// form. A host that is not a bare host name or IP address (<see cref="DataSourceHostName"/>) matches nothing.
    /// </summary>
    internal static bool IsAllowed(string host, IReadOnlyList<string> allowedHosts)
    {
        var name = DataSourceHostName.Canonical(host);
        if (name == null)
        {
            return false;
        }

        var isName = !DataSourceHostName.IsIpAddress(host);
        foreach (var entry in allowedHosts)
        {
            var trimmed = entry.Trim();
            if (trimmed.StartsWith("*.", StringComparison.Ordinal))
            {
                var domain = DataSourceHostName.Name(trimmed[2..]);
                if (isName && domain != null && name.EndsWith($".{domain}", StringComparison.Ordinal))
                {
                    return true;
                }

                continue;
            }

            if (DataSourceHostName.Canonical(trimmed) == name)
            {
                return true;
            }
        }

        return false;
    }

    private DataSourceConnectionOptionVerdict? Inspect(DataSourceType dataSourceType, DatabaseEngineType? engine, string connectionData)
    {
        var inspector = _inspectors
            .Where(x => x.DataSourceType == dataSourceType)
            .Where(x => x.DatabaseEngineType == engine)
            .LastOrDefault();
        if (inspector == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(connectionData))
        {
            return DataSourceConnectionOptionVerdict.NotReadable();
        }

        try
        {
            return inspector.Inspect(connectionData);
        }
        catch (Exception ex) when (DataSourceTransportPolicy.IsParserFailure(ex))
        {
            // A parser's message can quote the connection data: only the exception type and the engine are logged.
            logger.LogDebug(
                "The {DataSourceType} {DatabaseEngineType} connection option check could not read the connection data ({ExceptionType})",
                dataSourceType,
                engine,
                ex.GetType().Name);

            return DataSourceConnectionOptionVerdict.NotReadable();
        }
        catch (Exception ex)
        {
            logger.LogError(
                "The {DataSourceType} {DatabaseEngineType} connection option check failed unexpectedly ({ExceptionType}); the connection is treated as not readable",
                dataSourceType,
                engine,
                ex.GetType().Name);

            return DataSourceConnectionOptionVerdict.NotReadable();
        }
    }

    private bool IsLifted(DataSourceConnectionOptionVerdict verdict)
    {
        if (transportOptions.AllowInsecure)
        {
            return true;
        }

        return verdict.Risk == DataSourceConnectionOptionRisk.HostCredentials && connectionOptions.AllowHostCredentials;
    }

}

/// <summary>
/// A connection the option check does not accept: <see cref="Reason"/> (for the log) and <see cref="Message"/> (for the
/// refusal) are fixed text naming the option or the host list, never a value read from the connection data.
/// </summary>
internal sealed record DataSourceConnectionOptionProblem(string Reason, bool Unreadable, string Message);
