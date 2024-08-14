namespace Semantico.Core.Worker
{
    public interface IJobService
    {
        Task ExecuteQuery(int subscriptionId);
    }
}
