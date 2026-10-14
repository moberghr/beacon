namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// Work refused right before its SQL ran (<see cref="IExecutionAccessCheck"/>), already logged. A request answers it as
/// 403; a scheduled run records itself as skipped. <see cref="DataSourceIds"/>: the data sources the refused work would
/// have read.
/// </summary>
internal sealed class ExecutionRefusedException(IReadOnlyList<int> dataSourceIds, string message)
    : Models.BeaconForbiddenException(message)
{
    public IReadOnlyList<int> DataSourceIds { get; } = dataSourceIds;
}
