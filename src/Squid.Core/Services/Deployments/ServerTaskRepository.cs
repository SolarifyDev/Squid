using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Domain.Deployments;

namespace Squid.Core.Services.Deployments;

public class ServerTaskRepository : IServerTaskRepository
{
    private readonly IServerTaskDataProvider _serverTaskDataProvider;

    public ServerTaskRepository(IServerTaskDataProvider serverTaskDataProvider)
    {
        _serverTaskDataProvider = serverTaskDataProvider;
    }

    public async Task AddAsync(Message.Domain.Deployments.ServerTask task)
    {
        await _serverTaskDataProvider.AddServerTaskAsync(task).ConfigureAwait(false);
    }

    public async Task<Message.Domain.Deployments.ServerTask?> GetPendingTaskAsync()
    {
        return await _serverTaskDataProvider.GetPendingTaskAsync().ConfigureAwait(false);
    }

    public async Task UpdateStateAsync(int taskId, string state)
    {
        await _serverTaskDataProvider.UpdateServerTaskStateAsync(taskId, state).ConfigureAwait(false);
    }

    public async Task<List<Message.Domain.Deployments.ServerTask>> GetAllAsync()
    {
        return await _serverTaskDataProvider.GetAllServerTasksAsync().ConfigureAwait(false);
    }
}
