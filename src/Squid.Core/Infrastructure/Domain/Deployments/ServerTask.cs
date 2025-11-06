using System;

namespace Squid.Core.Infrastructure.Domain.Deployments
{
    /// <summary>
    /// 表示部署任务（ServerTask），用于记录自动化部署的调度、状态、日志等信息。
    /// </summary>
    public class ServerTask : IEntity
    {
        /// <summary>
        /// 主键，自增
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// 关联的部署ID
        /// </summary>
        public int DeploymentId { get; set; }

        /// <summary>
        /// 任务状态（Pending, Running, Succeeded, Failed）
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// 执行日志
        /// </summary>
        public string Log { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 开始执行时间
        /// </summary>
        public DateTime? StartedAt { get; set; }

        /// <summary>
        /// 完成时间
        /// </summary>
        public DateTime? FinishedAt { get; set; }

        // 可扩展：调度优先级、重试次数、触发人等
    }
}
