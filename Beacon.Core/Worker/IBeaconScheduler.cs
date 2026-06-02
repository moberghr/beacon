namespace Beacon.Core.Worker;

public interface IBeaconScheduler
{
    public void AddOrUpdate(int subscriptionId, string subscriptionName, string cron);

    public void Remove(int subscriptionId, string subscriptionName);

    void AddOrUpdateDataQualityJob(int contractId, string contractName, string cron)
        => throw new NotImplementedException("Data quality scheduling not implemented in this host");

    void RemoveDataQualityJob(int contractId, string contractName)
        => throw new NotImplementedException("Data quality scheduling not implemented in this host");
}