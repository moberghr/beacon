using Beacon.Core.Worker;
using Warp.Core.Handlers;

namespace Beacon.SampleProject.Warp.Jobs;

// Recurring jobs registered per-subscription / per-data-contract via IBeaconScheduler. The cron
// schedule lives on the Warp recurring-job definition; these handlers run one execution.

public sealed class ExecuteSubscriptionQueryJob : IJob
{
    public int SubscriptionId { get; init; }
}

public sealed class ExecuteSubscriptionQueryJobHandler(IJobService jobService) : IJobHandler<ExecuteSubscriptionQueryJob>
{
    public Task HandleAsync(ExecuteSubscriptionQueryJob message, CancellationToken cancellationToken)
        => jobService.ExecuteQuery(message.SubscriptionId, cancellationToken);
}

public sealed class EvaluateDataContractJob : IJob
{
    public int ContractId { get; init; }
}

public sealed class EvaluateDataContractJobHandler(IJobService jobService) : IJobHandler<EvaluateDataContractJob>
{
    public Task HandleAsync(EvaluateDataContractJob message, CancellationToken cancellationToken)
        => jobService.EvaluateDataContract(message.ContractId, cancellationToken);
}
