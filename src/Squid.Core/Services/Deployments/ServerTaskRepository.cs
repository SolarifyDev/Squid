using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Message.Domain.Deployments;

namespace Squid.Core.Services.Deployments;

public class ServerTaskRepository : IServerTaskRepository
{
    private readonly SquidDbContext _dbContext;

    public ServerTaskRepository(SquidDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(ServerTask task)
    {
        await _dbContext.Set<ServerTask>().AddAsync(task);
        await _dbContext.SaveChangesAsync();
    }

    public async Task<ServerTask?> GetPendingTaskAsync()
    {
        return await _dbContext.Set<ServerTask>()
            .Where(t => t.State == "Pending")
            .OrderBy(t => t.QueueTime)
            .FirstOrDefaultAsync();
    }

    public async Task UpdateStateAsync(int taskId, string state)
    {
        var task = await _dbContext.Set<ServerTask>().FindAsync(taskId);

        if (task != null)
        {
            task.State = state;
            await _dbContext.SaveChangesAsync();
        }
    }

    public async Task<List<ServerTask>> GetAllAsync()
    {
        return await _dbContext.Set<ServerTask>().ToListAsync();
    }
}
