using Squid.Core.Services.Deployments.Deployment;
using Squid.Core.Services.Deployments.DeploymentCompletion;
using Squid.Message.Domain.Deployments;
using System.Linq;
using System.Text;
using System.Text.Json;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Commands.Tentacle;
using Squid.Core.Services.Tentacle;

namespace Squid.Core.Services.Deployments;


public interface IDeploymentTaskBackgroundService : IScopedDependency
{
    Task RunAsync(CancellationToken cancellationToken = default);
}

public class DeploymentTaskBackgroundService : IDeploymentTaskBackgroundService
{
    private readonly HalibutRuntime _halibutRuntime;
    private readonly IYamlNuGetPacker _yamlNuGetPacker;
    private readonly IDeploymentPlanService _planService;
    private readonly IDeploymentTargetFinder _targetFinder;
    private readonly IGenericDataProvider _genericDataProvider;
    private readonly IDeploymentVariableResolver _variableResolver;
    private readonly IServerTaskDataProvider _serverTaskDataProvider;
    private readonly IDeploymentDataProvider _deploymentDataProvider;
    private readonly IDeploymentCompletionDataProvider _deploymentCompletionDataProvider;
    private readonly IEnumerable<IActionYamlGenerator> _actionYamlGenerators;
    
    public DeploymentTaskBackgroundService(
        IDeploymentPlanService planService,
        IDeploymentVariableResolver variableResolver,
        IDeploymentTargetFinder targetFinder,
        IDeploymentDataProvider deploymentDataProvider,
        IDeploymentCompletionDataProvider deploymentCompletionDataProvider,
        IGenericDataProvider genericDataProvider,
        IServerTaskDataProvider serverTaskDataProvider,
        IEnumerable<IActionYamlGenerator> actionYamlGenerators,
        IYamlNuGetPacker yamlNuGetPacker,
        HalibutRuntime halibutRuntime)
    {
        _planService = planService;
        _variableResolver = variableResolver;
        _targetFinder = targetFinder;
        _deploymentDataProvider = deploymentDataProvider;
        _deploymentCompletionDataProvider = deploymentCompletionDataProvider;
        _genericDataProvider = genericDataProvider;
        _serverTaskDataProvider = serverTaskDataProvider;
        _actionYamlGenerators = actionYamlGenerators;
        _yamlNuGetPacker = yamlNuGetPacker;
        _halibutRuntime = halibutRuntime;
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var task = await _serverTaskDataProvider.GetAndLockPendingTaskAsync(stoppingToken).ConfigureAwait(false);

            if (task != null)
            {
                Log.Information("Start processing task {TaskId}", task.Id);

                try
                {
                    await ProcessDeploymentTaskAsync(task, stoppingToken).ConfigureAwait(false);

                    await _genericDataProvider.ExecuteInTransactionAsync(async (cancellationToken) =>
                    {
                        await _serverTaskDataProvider.UpdateServerTaskStateAsync(
                            task.Id, "Success", cancellationToken: cancellationToken).ConfigureAwait(false);
                    }, stoppingToken).ConfigureAwait(false);

                    Log.Information("Task {TaskId} completed successfully", task.Id);
                }
                catch (Exception ex)
                {
                    await _genericDataProvider.ExecuteInTransactionAsync(
                        async (cancellationToken) =>
                        {
                            await _serverTaskDataProvider.UpdateServerTaskStateAsync(task.Id, "Failed", cancellationToken: cancellationToken).ConfigureAwait(false);
                        }, stoppingToken).ConfigureAwait(false);

                    Log.Error(ex, "Task {TaskId} failed: {ErrorMessage}", task.Id, ex.Message);
                }
            }
            else
            {
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessDeploymentTaskAsync(Message.Domain.Deployments.ServerTask task, CancellationToken cancellationToken)
    {
        var deployment = await _deploymentDataProvider.GetDeploymentByTaskIdAsync(task.Id, cancellationToken).ConfigureAwait(false);

        if (deployment == null) throw new InvalidOperationException($"No deployment found for task {task.Id}");

        try
        {
            // 从嵌入资源中读取 DeployByCalamari 部署脚本内容
            var deployByCalamariScript = UtilService.GetEmbeddedScriptContent("DeployByCalamari.ps1");

            // 从嵌入资源中读取 ExtractCalamariPackage 安装脚本内容
            var extractCalamariPackageScript = UtilService.GetEmbeddedScriptContent("ExtractCalamariPackage.ps1");

            // TODO：拿到Calamari安装文件流

            // TODO：把ExtractCalamariPackage和安装文件流用StartScriptCommand传去目标机器执行

            Log.Information("Generating deployment plan for deployment {DeploymentId}", deployment.Id);

            // 1. 生成部署计划（包含流程快照）
            // 调用 PlanService 生成当前 Deployment 的部署计划和流程快照
            var plan = await _planService.GeneratePlanAsync(deployment.Id, cancellationToken).ConfigureAwait(false);

            Log.Information("Resolving variables for deployment {DeploymentId}", deployment.Id);

            // 2. 解析变量（包含变量快照）
            // 调用 VariableResolver 解析当前 Deployment 的变量并生成变量快照
            var variables = await _variableResolver.ResolveVariablesAsync(deployment.Id, cancellationToken).ConfigureAwait(false);

            Log.Information("Finding targets for deployment {DeploymentId}", deployment.Id);

            // 3. 筛选目标机器
            // 调用 DeploymentTargetFinder 根据 Deployment 和环境筛选出需要部署的机器列表
            var targets = await _targetFinder.FindTargetsAsync(deployment.Id).ConfigureAwait(false);

            if (!targets.Any()) throw new InvalidOperationException($"No target machines found for deployment {deployment.Id}");

            Log.Information("Found {TargetCount} target machines for deployment {DeploymentId}", targets.Count, deployment.Id);

            // 4. 转换ProcessSnapshot为DeploymentStepDto列表
            var steps = ConvertProcessSnapshotToSteps(plan.ProcessSnapshot);

            // 调用 Yaml 相关服务为每个步骤生成对应的 YAML 流
            var yamlStreams = await GenerateYamlStreamsAsync(steps, cancellationToken).ConfigureAwait(false);

            Log.Information("Creating YAML NuGet package for deployment {DeploymentId}", deployment.Id);

            // 把所有 YAML 流打包成一个 NuGet 包的字节数组，准备发送给目标机器
            var yamlNuGetPackageBytes = CreateYamlNuGetPackage(yamlStreams);

            // 根据解析后的变量生成 variables.json、sensitiveVariables.json 以及对应的密码
            var (variableJsonStream, sensitiveVariableJsonStream, sensitiveVariablesPassword) = CreateVariableFileStreamsAndPassword(variables);

            // 使用 StartScriptCommand 调用 Tentacle，把脚本和 NuGet 包、变量文件一并传到目标机器执行
            var scriptExecutions = await StartDeployByCalamariScriptsAsync(
                deployByCalamariScript,
                yamlNuGetPackageBytes,
                variableJsonStream,
                sensitiveVariableJsonStream,
                sensitiveVariablesPassword,
                targets,
                cancellationToken).ConfigureAwait(false);

            // 观察远程脚本执行状态和输出日志，并根据结果判断本次部署是否成功
            var executionSuccess = await ObserveDeploymentScriptsAsync(scriptExecutions, cancellationToken).ConfigureAwait(false);

            // 7. 记录部署完成状态
            await RecordDeploymentCompletionAsync(deployment.Id, executionSuccess,
                executionSuccess ? "Deployment completed successfully" : "Deployment completed with errors").ConfigureAwait(false);

            if (executionSuccess)
            {
                Log.Information("Deployment {DeploymentId} completed successfully", deployment.Id);
            }
            else
            {
                Log.Error("Deployment {DeploymentId} completed with errors", deployment.Id);
            }
        }
        catch (Exception ex)
        {
            await RecordDeploymentCompletionAsync(deployment.Id, false, ex.Message).ConfigureAwait(false);

            Log.Error(ex, "Deployment {DeploymentId} failed with exception", deployment.Id);

            throw;
        }
    }
    
    private async Task<List<(Message.Domain.Deployments.Machine Machine, IAsyncScriptService ScriptClient, ScriptTicket Ticket)>> StartDeployByCalamariScriptsAsync(
        string deployByCalamariScript,
        byte[] yamlNuGetPackageBytes,
        Stream variableJsonStream,
        Stream sensitiveVariableJsonStream,
        string sensitiveVariablesPassword,
        List<Message.Domain.Deployments.Machine> targets,
        CancellationToken cancellationToken)
    {
        var result = new List<(Message.Domain.Deployments.Machine Machine, IAsyncScriptService ScriptClient, ScriptTicket Ticket)>();

        if (targets == null || targets.Count == 0)
        {
            throw new InvalidOperationException("No target machines to execute DeployByCalamari script");
        }

        var packageBytes = yamlNuGetPackageBytes ?? Array.Empty<byte>();
        var variableBytes = ReadAllBytes(variableJsonStream);
        var sensitiveBytes = ReadAllBytes(sensitiveVariableJsonStream);

        const string packageFileName = "squid.yaml.package.nupkg";
        const string variableFileName = "variables.json";
        const string sensitiveVariableFileName = "sensitiveVariables.json";

        var packageFilePath = $".\\{packageFileName}";
        var variableFilePath = $".\\{variableFileName}";
        var sensitiveVariableFilePath = $".\\{sensitiveVariableFileName}";

        var calamariVersion = GetCalamariVersion();

        var scriptBody = deployByCalamariScript
            .Replace("{{CalamariVersion}}", calamariVersion, StringComparison.Ordinal)
            .Replace("{{PackageFilePath}}", packageFilePath, StringComparison.Ordinal)
            .Replace("{{VariableFilePath}}", variableFilePath, StringComparison.Ordinal)
            .Replace("{{SensitiveVariableFile}}", sensitiveVariableFilePath, StringComparison.Ordinal);

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var endpoint = ParseMachineEndpoint(target);

            if (endpoint == null)
            {
                Log.Warning("Skipping machine {MachineName} because endpoint could not be parsed", target.Name);
                continue;
            }

            var scriptFiles = new[]
            {
                new ScriptFile(packageFileName, DataStream.FromBytes(packageBytes), null),
                new ScriptFile(variableFileName, DataStream.FromBytes(variableBytes), null),
                new ScriptFile(sensitiveVariableFileName, DataStream.FromBytes(sensitiveBytes), sensitiveVariablesPassword)
            };

            var command = new StartScriptCommand(
                scriptBody,
                ScriptIsolationLevel.FullIsolation,
                TimeSpan.FromMinutes(30),
                null,
                Array.Empty<string>(),
                null,
                scriptFiles);

            Log.Information("Starting DeployByCalamari script on machine {MachineName}", target.Name);

            var scriptClient = _halibutRuntime.CreateAsyncClient<IScriptService, IAsyncScriptService>(endpoint);

            var ticket = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

            result.Add((target, scriptClient, ticket));
        }

        return result;
    }

    private async Task<bool> ObserveDeploymentScriptsAsync(
        List<(Message.Domain.Deployments.Machine Machine, IAsyncScriptService ScriptClient, ScriptTicket Ticket)> scriptExecutions,
        CancellationToken cancellationToken)
    {
        if (scriptExecutions == null || scriptExecutions.Count == 0)
        {
            throw new InvalidOperationException("No script executions to observe for deployment");
        }

        var overallSuccess = true;

        foreach (var execution in scriptExecutions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scriptStatusResponse = new ScriptStatusResponse(
                execution.Ticket,
                ProcessState.Pending,
                0,
                new List<ProcessOutput>(),
                0);

            var logs = new List<ProcessOutput>();

            while (scriptStatusResponse.State != ProcessState.Complete)
            {
                cancellationToken.ThrowIfCancellationRequested();

                scriptStatusResponse = await execution.ScriptClient.GetStatusAsync(
                    new ScriptStatusRequest(execution.Ticket, scriptStatusResponse.NextLogSequence)).ConfigureAwait(false);

                logs.AddRange(scriptStatusResponse.Logs);

                if (scriptStatusResponse.State != ProcessState.Complete)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
            }

            var completeResponse = await execution.ScriptClient.CompleteScriptAsync(
                new CompleteScriptCommand(execution.Ticket, scriptStatusResponse.NextLogSequence)).ConfigureAwait(false);

            logs.AddRange(completeResponse.Logs);

            var orderedLogs = logs
                .OrderBy(l => l.Occurred)
                .ToList();

            foreach (var log in orderedLogs)
            {
                Log.Information(
                    "[Deployment Script] Machine={MachineName}, Time={Time}, Source={Source}, Message={Message}",
                    execution.Machine.Name,
                    log.Occurred,
                    log.Source,
                    log.Text);
            }

            var success = completeResponse.ExitCode == 0;

            if (!success)
            {
                overallSuccess = false;

                Log.Error(
                    "Deployment script failed on machine {MachineName} with exit code {ExitCode}",
                    execution.Machine.Name,
                    completeResponse.ExitCode);
            }
            else
            {
                Log.Information(
                    "Deployment script completed successfully on machine {MachineName}",
                    execution.Machine.Name);
            }
        }

        return overallSuccess;
    }

    private byte[] CreateYamlNuGetPackage(Dictionary<string, Stream> yamlStreams)
    {
        if (yamlStreams == null || yamlStreams.Count == 0)
        {
            Log.Information("No YAML streams to pack into NuGet package");
            return Array.Empty<byte>();
        }

        return _yamlNuGetPacker.CreateNuGetPackageFromYamlStreams(yamlStreams);
    }

    private static string GetCalamariVersion()
    {
        var version = System.Environment.GetEnvironmentVariable("SQUID_CALAMARI_VERSION");

        if (string.IsNullOrWhiteSpace(version))
        {
            version = "0.1.0";
        }

        return version;
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream == null)
        {
            return Array.Empty<byte>();
        }

        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private ServiceEndPoint? ParseMachineEndpoint(Message.Domain.Deployments.Machine machine)
    {
        try
        {
            if (string.IsNullOrEmpty(machine.Json))
            {
                return null;
            }

            var machineConfig = JsonSerializer.Deserialize<MachineConfigurationDto>(machine.Json);

            if (machineConfig?.Endpoint == null)
            {
                return null;
            }

            return new ServiceEndPoint(machineConfig.Endpoint.Uri, machineConfig.Endpoint.Thumbprint, HalibutTimeoutsAndLimits.RecommendedValues());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse machine endpoint for machine {MachineName}", machine.Name);
            return null;
        }
    }

    private async Task<Dictionary<string, Stream>> GenerateYamlStreamsAsync(
        List<Squid.Message.Models.Deployments.Process.DeploymentStepDto> steps,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in steps.OrderBy(p => p.StepOrder))
        {
            foreach (var action in step.Actions.OrderBy(p => p.ActionOrder))
            {
                var generator = _actionYamlGenerators.FirstOrDefault(p => p.CanHandle(action));

                if (generator == null)
                {
                    continue;
                }

                var yamlFiles = await generator.GenerateAsync(step, action, cancellationToken).ConfigureAwait(false);

                if (yamlFiles == null || yamlFiles.Count == 0)
                {
                    continue;
                }

                foreach (var yamlFile in yamlFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var fileName = $"{step.StepOrder:D3}-{action.ActionOrder:D3}-{yamlFile.Key}";

                    if (result.ContainsKey(fileName))
                    {
                        continue;
                    }

                    var stream = new MemoryStream(yamlFile.Value);

                    result[fileName] = stream;
                }
            }
        }

        return result;
    }

    private List<Squid.Message.Models.Deployments.Process.DeploymentStepDto> ConvertProcessSnapshotToSteps(
        Squid.Message.Models.Deployments.Process.ProcessSnapshotData processSnapshot)
    {
        var steps = new List<Squid.Message.Models.Deployments.Process.DeploymentStepDto>();

        foreach (var stepSnap in processSnapshot.StepSnapshots.OrderBy(p => p.StepOrder))
        {
            var step = new Squid.Message.Models.Deployments.Process.DeploymentStepDto
            {
                Id = stepSnap.Id,
                ProcessId = processSnapshot.Id,
                StepOrder = stepSnap.StepOrder,
                Name = stepSnap.Name,
                StepType = stepSnap.StepType,
                Condition = stepSnap.Condition,
                StartTrigger = "",
                PackageRequirement = "",
                IsDisabled = false,
                IsRequired = true,
                CreatedAt = stepSnap.CreatedAt,
                Properties = stepSnap.Properties.Select(kvp =>
                    new Squid.Message.Models.Deployments.Process.DeploymentStepPropertyDto
                    {
                        StepId = stepSnap.Id,
                        PropertyName = kvp.Key,
                        PropertyValue = kvp.Value
                    }).ToList(),
                Actions = stepSnap.Actions.Select(action =>
                    new Squid.Message.Models.Deployments.Process.DeploymentActionDto
                    {
                        Id = action.Id,
                        StepId = stepSnap.Id,
                        ActionOrder = action.ActionOrder,
                        Name = action.Name,
                        ActionType = action.ActionType,
                        WorkerPoolId = action.WorkerPoolId,
                        IsDisabled = action.IsDisabled,
                        IsRequired = action.IsRequired,
                        CanBeUsedForProjectVersioning = action.CanBeUsedForProjectVersioning,
                        CreatedAt = action.CreatedAt,
                        Properties = action.Properties.Select(kvp =>
                            new Squid.Message.Models.Deployments.Process.DeploymentActionPropertyDto
                            {
                                ActionId = action.Id,
                                PropertyName = kvp.Key,
                                PropertyValue = kvp.Value
                            }).ToList(),
                        Environments = action.Environments,
                        Channels = action.Channels,
                        MachineRoles = action.MachineRoles
                    }).ToList()
            };

            steps.Add(step);
        }
        return steps;
    }

    private (Stream variableJsonStream, Stream sensitiveVariableJsonStream, string password) CreateVariableFileStreamsAndPassword(
        Squid.Message.Models.Deployments.Variable.VariableSetSnapshotData variables)
    {
        if (variables == null || variables.Variables == null || variables.Variables.Count == 0)
        {
            var emptyJson = "{}";
            var emptyBytes = Encoding.UTF8.GetBytes(emptyJson);
            return (new MemoryStream(emptyBytes), new MemoryStream(emptyBytes), string.Empty);
        }

        var nonSensitiveVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sensitiveVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var variable in variables.Variables)
        {
            if (variable == null || string.IsNullOrWhiteSpace(variable.Name))
                continue;

            var value = variable.Value ?? string.Empty;

            if (variable.IsSensitive)
            {
                sensitiveVariables[variable.Name] = value;
            }
            else
            {
                nonSensitiveVariables[variable.Name] = value;
            }
        }

        // 普通变量JSON
        var variableJson = JsonSerializer.Serialize(nonSensitiveVariables);
        var variableStream = new MemoryStream(Encoding.UTF8.GetBytes(variableJson));

        // 敏感变量处理
        var password = string.Empty;
        Stream sensitiveStream;

        if (sensitiveVariables.Count > 0)
        {
            password = Guid.NewGuid().ToString("N");

            var sensitiveJson = JsonSerializer.Serialize(sensitiveVariables);
            var encryption = new CalamariCompatibleEncryption(password);
            var encryptedBytes = encryption.Encrypt(sensitiveJson);  // 加密整个JSON

            sensitiveStream = new MemoryStream(encryptedBytes);
        }
        else
        {
            var emptyJson = "{}";
            sensitiveStream = new MemoryStream(Encoding.UTF8.GetBytes(emptyJson));
        }

        return (variableStream, sensitiveStream, password);
    }

    private async Task RecordDeploymentCompletionAsync(int deploymentId, bool success, string message)
    {
        // 获取部署信息以获取SpaceId和ReleaseId
        var deployment = await _deploymentDataProvider.GetDeploymentByIdAsync(deploymentId).ConfigureAwait(false);

        var completion = new Message.Domain.Deployments.DeploymentCompletion
        {
            DeploymentId = deploymentId,
            ReleaseId = deployment?.ReleaseId,
            CompletedTime = DateTimeOffset.UtcNow,
            State = success ? "Success" : "Failed",
            SpaceId = deployment?.SpaceId ?? 1, // 如果找不到部署，使用默认SpaceId
            SequenceNumber = 0 // 这个字段会由数据库自动生成
        };

        await _deploymentCompletionDataProvider.AddDeploymentCompletionAsync(completion).ConfigureAwait(false);

        Log.Information("Recorded deployment completion for deployment {DeploymentId} (Release {ReleaseId}): {Status}",
            deploymentId, deployment?.ReleaseId, success ? "Success" : "Failed");
    }
    
    public class MachineConfigurationDto
    {
        public MachineEndpointDto? Endpoint { get; set; }
    }

    public class MachineEndpointDto
    {
        public string Uri { get; set; } = string.Empty;

        public string Thumbprint { get; set; } = string.Empty;
    }
}
