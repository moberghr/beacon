namespace Semantico.Api.Worker;

public interface IJobService
{
    Task ExecuteQuery(int subscriptionId);
}