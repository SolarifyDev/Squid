using Squid.Message.Domain.Deployments;

namespace Squid.Core.Services.Deployments;

public interface IServerTaskRepository
{
    Task AddAsync(Message.Domain.Deployments.ServerTask task);

    Task<Message.Domain.Deployments.ServerTask?> GetPendingTaskAsync();

    Task UpdateStateAsync(int taskId, string state);

    Task<List<Message.Domain.Deployments.ServerTask>> GetAllAsync();
}
