using Beacon.Core.Authorization;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.DataSources;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class UpdateDataSourceHandler(IDataSourceService dataSourceService)
    : IRequestHandler<UpdateDataSourceCommand>
{
    public async Task Handle(UpdateDataSourceCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("A data source needs a name.");
        }

        var data = new DataSourceData
        {
            DataSourceId = request.DataSourceId,
            Name = request.Name,
            DatabaseEngineType = request.DatabaseEngineType,
            ConnectionString = request.ConnectionString,
            ConfirmTargetChange = request.ConfirmTargetChange,
            MetadataLoadingEnabled = request.MetadataLoadingEnabled,
            MetadataMaxTables = request.MetadataMaxTables,
            MetadataMaxColumnsPerTable = request.MetadataMaxColumnsPerTable,
            MetadataLoadTableNamesOnly = request.MetadataLoadTableNamesOnly,
            MetadataExcludeSchemas = request.MetadataExcludeSchemas,
            MetadataIncludeSchemas = request.MetadataIncludeSchemas
        };

        await dataSourceService.UpdateDataSource(data, cancellationToken);
    }
}

/// <summary>
/// Edits a data source (an Admin or a DataSourceAdmin), and replaces its connection: the way its credentials are rotated.
/// A null or empty <paramref name="ConnectionString"/> keeps the stored connection, which is never shown. A new connection
/// must authenticate the server over TLS. A connection to another engine, host, port or database (or one whose target
/// cannot be read) is saved only with <paramref name="ConfirmTargetChange"/>, otherwise 409; a confirmed change removes the
/// column values sampled from the old target and enqueues a metadata re-scan (the old target's tables are shown until it
/// finishes). A database source needs an engine, and an engine change needs the connection entered again. A metadata
/// option left null keeps the stored value; an empty schema list clears it. Host-managed sources are refused. Recorded as
/// <c>data_sources.credentials_rotated</c> when the connection changed, <c>data_sources.data_source_updated</c> otherwise,
/// naming the changed fields and whether the target changed, never a value.
/// </summary>
[ProjectScopeExempt("data-source administration, gated by the DataSourceAdmin permission", Policy = BeaconPermissions.DataSourceAdminPolicy)]
public record UpdateDataSourceCommand(
    int DataSourceId,
    string Name,
    DatabaseEngineType? DatabaseEngineType,
    string? ConnectionString,
    bool? MetadataLoadingEnabled,
    int? MetadataMaxTables,
    int? MetadataMaxColumnsPerTable,
    bool? MetadataLoadTableNamesOnly,
    List<string>? MetadataExcludeSchemas,
    List<string>? MetadataIncludeSchemas,
    bool ConfirmTargetChange = false) : IRequest
{
    // The compiler-generated ToString prints every member; the secrets are printed redacted.
    protected virtual bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"DataSourceId = {DataSourceId}, Name = {Name}, DatabaseEngineType = {DatabaseEngineType}, ConnectionString = {RedactedPrint.Of(ConnectionString)}, MetadataLoadingEnabled = {MetadataLoadingEnabled}, MetadataMaxTables = {MetadataMaxTables}, MetadataMaxColumnsPerTable = {MetadataMaxColumnsPerTable}, MetadataLoadTableNamesOnly = {MetadataLoadTableNamesOnly}, MetadataExcludeSchemas = {MetadataExcludeSchemas}, MetadataIncludeSchemas = {MetadataIncludeSchemas}, ConfirmTargetChange = {ConfirmTargetChange}");

        return true;
    }
}
