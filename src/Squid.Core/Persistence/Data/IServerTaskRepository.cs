using System.Collections.Generic;
using System.Threading.Tasks;
using Squid.Core.Infrastructure.Domain.Deployments;

namespace Squid.Core.Persistence.Data
{
    /// <summary>
    /// ServerTask 仓储接口，支持任务的持久化与调度。
    /// </summary>
    public interface IServerTaskRepository
    {
        /// <summary>
        /// 新增任务
        /// </summary>
        Task AddAsync(ServerTask task);

        /// <summary>
        /// 获取一个待执行的任务（Pending）
        /// </summary>
        Task<ServerTask> GetPendingTaskAsync();

        /// <summary>
        /// 更新任务状态
        /// </summary>
        Task UpdateStatusAsync(int taskId, string status);

        /// <summary>
        /// 获取所有任务
        /// </summary>
        Task<List<ServerTask>> GetAllAsync();

        // 可扩展：根据状态/时间/优先级等查询
    }
}
