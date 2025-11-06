using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Squid.Core.Infrastructure.Domain.Deployments;

namespace Squid.Core.Persistence.Data
{
    /// <summary>
    /// 内存实现的 ServerTask 仓储，便于开发和测试，后续可替换为数据库实现。
    /// </summary>
    public class InMemoryServerTaskRepository : IServerTaskRepository
    {
        private readonly List<ServerTask> _tasks = new();
        private readonly object _lock = new();

        /// <inheritdoc />
        public Task AddAsync(ServerTask task)
        {
            lock (_lock)
            {
                task.Id = _tasks.Count > 0 ? _tasks.Max(t => t.Id) + 1 : 1;
                _tasks.Add(task);
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<ServerTask> GetPendingTaskAsync()
        {
            lock (_lock)
            {
                var task = _tasks.FirstOrDefault(t => t.Status == "Pending");
                return Task.FromResult(task);
            }
        }

        /// <inheritdoc />
        public Task UpdateStatusAsync(int taskId, string status)
        {
            lock (_lock)
            {
                var task = _tasks.FirstOrDefault(t => t.Id == taskId);
                if (task != null)
                {
                    task.Status = status;
                }
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<List<ServerTask>> GetAllAsync()
        {
            lock (_lock)
            {
                return Task.FromResult(_tasks.ToList());
            }
        }
    }
}
