namespace Semantico.Core.Worker;

public interface ISemanticoScheduler
{
    public void AddOrUpdate(int subscriptionId, string subscriptionName, string cron);

    public void Remove(int subscriptionId, string subscriptionName);
}