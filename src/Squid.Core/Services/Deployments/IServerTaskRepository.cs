using Squid.Message.Domain.Deployments;

namespace Squid.Core.Services.Deployments;

public interface IServerTaskRepository
{
    Task AddAsync(ServerTask task);

    Task<ServerTask?> GetPendingTaskAsync();

    Task UpdateStateAsync(int taskId, string state);

    Task<List<ServerTask>> GetAllAsync();
}
