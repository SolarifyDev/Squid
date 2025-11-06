using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squid.Core.Services.Deployments.Variable;
using Squid.Core.Services.Deployments.Process;
using Squid.Core.Logging;

namespace Squid.Core.Services.Deployments
{
    /// <summary>
    /// 部署计划生成服务：负责合并变量快照、解析流程步骤、生成可执行计划
    /// </summary>
    public class DeploymentPlanner : IDeploymentPlanner
    {
        private readonly IHybridVariableSnapshotService _variableSnapshotService;
        private readonly IDeploymentProcessDataProvider _processDataProvider;
        private readonly ILogger _logger;

        public DeploymentPlanner(
            IHybridVariableSnapshotService variableSnapshotService,
            IDeploymentProcessDataProvider processDataProvider,
            ILogger logger)
        {
            _variableSnapshotService = variableSnapshotService;
            _processDataProvider = processDataProvider;
            _logger = logger;
        }

        /// <summary>
        /// 生成部署计划（合并变量、解析步骤/动作/目标）
        /// </summary>
        public async Task<DeploymentPlan> GeneratePlanAsync(DeploymentTask task, CancellationToken cancellationToken)
        {
            // 1. 加载部署流程（DeploymentProcess/Step/Action）
            var process = await _processDataProvider.GetDeploymentProcessByDeploymentIdAsync(task.DeploymentId, cancellationToken);
            var steps = await _processDataProvider.GetDeploymentStepsByProcessIdAsync(process.Id, cancellationToken);

            // 2. 合并并解析变量快照
            // 假设Deployment有VariableSetId
            int variableSetId = process.VariableSetId;
            var variableSnapshotData = await _variableSnapshotService.LoadCompleteVariableSetAsync(variableSetId, cancellationToken);
            var variables = MergeAndResolveVariables(variableSnapshotData);

            // 3. 组装DeploymentPlan
            var plan = new DeploymentPlan
            {
                Steps = steps.OrderBy(s => s.StepOrder).Select(step => new DeploymentStep
                {
                    StepOrder = step.StepOrder,
                    Name = step.Name,
                    IsParallel = step.StepType == "parallel", // 假设StepType区分并发/串行
                    Actions = step.Actions.Select(action => new DeploymentAction
                    {
                        Name = action.Name,
                        Target = action.Target // 目标机器/环境
                    }).ToList()
                }).ToList(),
                VariableSnapshot = variables
            };

            _logger.Info($"[DeploymentPlanner] 计划生成完成，包含步骤数: {plan.Steps.Count}");
            return plan;
        }

        /// <summary>
        /// 合并并解析变量（可递归插值、优先级覆盖等）
        /// </summary>
        private Dictionary<string, string> MergeAndResolveVariables(VariableSetSnapshotData snapshotData)
        {
            // 1. 合并多层变量（项目、环境、流程、租户、用户输入等）
            var variables = new Dictionary<string, string>();
            foreach (var variable in snapshotData.Variables.OrderBy(v => v.Priority)) // 假设有Priority
            {
                variables[variable.Name] = variable.Value;
            }

            // 2. 递归解析变量引用（如 #{DbPassword}）
            foreach (var key in variables.Keys.ToList())
            {
                variables[key] = ResolveVariableValue(variables[key], variables);
            }

            return variables;
        }

        /// <summary>
        /// 递归解析变量引用
        /// </summary>
        private string ResolveVariableValue(string value, Dictionary<string, string> variables, int depth = 0)
        {
            if (string.IsNullOrEmpty(value) || depth > 5) return value; // 防止死循环
            int start = value.IndexOf("#{");
            int end = value.IndexOf("}", start + 2);
            if (start >= 0 && end > start)
            {
                var varName = value.Substring(start + 2, end - start - 2);
                if (variables.TryGetValue(varName, out var varValue))
                {
                    var resolved = value.Substring(0, start) + varValue + value.Substring(end + 1);
                    return ResolveVariableValue(resolved, variables, depth + 1);
                }
            }
            return value;
        }
    }

    // 假设已有的快照数据结构
    public class VariableSetSnapshotData
    {
        public List<VariableSnapshotData> Variables { get; set; }
    }

    public class VariableSnapshotData
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public int Priority { get; set; }
    }
}
