using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Halibut;
using Halibut.Diagnostics;
using Squid.Core.Commands.Tentacle;
using Squid.Core.Extensions;
using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.Deployment;
using Squid.Core.Services.Deployments.DeploymentCompletion;
using Squid.Core.Services.Deployments.ExternalFeed;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Tentacle;
using Squid.Core.Settings.GithubPackage;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

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
    private readonly IAccountDataProvider _accountDataProvider;
    private readonly IReleaseDataProvider _releaseDataProvider;
    private readonly IGenericDataProvider _genericDataProvider;
    private readonly IDeploymentVariableResolver _variableResolver;
    private readonly IServerTaskDataProvider _serverTaskDataProvider;
    private readonly IDeploymentDataProvider _deploymentDataProvider;
    private readonly IExternalFeedDataProvider _externalFeedDataProvider;
    private readonly IEnumerable<IActionYamlGenerator> _actionYamlGenerators;
    private readonly CalamariGithubPackageSetting _calamariGithubPackageSetting;
    private readonly IDeploymentCompletionDataProvider _deploymentCompletionDataProvider;

    public DeploymentTaskBackgroundService(
        IYamlNuGetPacker yamlNuGetPacker,
        IDeploymentPlanService planService,
        IDeploymentTargetFinder targetFinder,
        IAccountDataProvider accountDataProvider,
        IReleaseDataProvider releaseDataProvider,
        IGenericDataProvider genericDataProvider,
        IDeploymentVariableResolver variableResolver,
        IServerTaskDataProvider serverTaskDataProvider,
        IDeploymentDataProvider deploymentDataProvider,
        IExternalFeedDataProvider externalFeedDataProvider,
        IEnumerable<IActionYamlGenerator> actionYamlGenerators,
        IDeploymentCompletionDataProvider deploymentCompletionDataProvider,
        HalibutRuntime halibutRuntime, CalamariGithubPackageSetting calamariGithubPackageSetting)
    {
        _planService = planService;
        _targetFinder = targetFinder;
        _halibutRuntime = halibutRuntime;
        _yamlNuGetPacker = yamlNuGetPacker;
        _variableResolver = variableResolver;
        _accountDataProvider = accountDataProvider;
        _genericDataProvider = genericDataProvider;
        _releaseDataProvider = releaseDataProvider;
        _actionYamlGenerators = actionYamlGenerators;
        _deploymentDataProvider = deploymentDataProvider;
        _serverTaskDataProvider = serverTaskDataProvider;
        _externalFeedDataProvider = externalFeedDataProvider;
        _calamariGithubPackageSetting = calamariGithubPackageSetting;
        _deploymentCompletionDataProvider = deploymentCompletionDataProvider;
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

                    await _genericDataProvider.ExecuteInTransactionAsync(
                        async cancellationToken =>
                        {
                            await _serverTaskDataProvider.UpdateServerTaskStateAsync(
                                task.Id, "Success", cancellationToken: cancellationToken).ConfigureAwait(false);
                        }, stoppingToken).ConfigureAwait(false);

                    Log.Information("Task {TaskId} completed successfully", task.Id);
                }
                catch (Exception ex)
                {
                    await _genericDataProvider.ExecuteInTransactionAsync(
                        async cancellationToken => { await _serverTaskDataProvider.UpdateServerTaskStateAsync(task.Id, "Failed", cancellationToken: cancellationToken).ConfigureAwait(false); }, stoppingToken).ConfigureAwait(false);

                    Log.Error(ex, "Task {TaskId} failed: {ErrorMessage}", task.Id, ex.Message);
                }
            }
            else
            {
                try
                {
                    await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task ProcessDeploymentTaskAsync(Persistence.Data.Domain.Deployments.ServerTask task, CancellationToken cancellationToken)
    {
        var deployment = await _deploymentDataProvider.GetDeploymentByTaskIdAsync(task.Id, cancellationToken).ConfigureAwait(false);

        if (deployment == null) throw new InvalidOperationException($"No deployment found for task {task.Id}");
        
        var release = await _releaseDataProvider.GetReleaseByIdAsync(deployment.ReleaseId, cancellationToken).ConfigureAwait(false);

        try
        {
            // 从嵌入资源中读取 DeployByCalamari 部署脚本内容
            var deployByCalamariScript = UtilService.GetEmbeddedScriptContent("DeployByCalamari.ps1");

            // 从嵌入资源中读取 ExtractCalamariPackage 安装脚本内容
            var extractCalamariPackageScript = UtilService.GetEmbeddedScriptContent("ExtractCalamariPackage.ps1");

            // 拿到Calamari安装包字节内容
            var calamariPackageBytes = await DownloadCalamariPackageAsync().ConfigureAwait(false);

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
            // 调用 DeploymentTargetFinder 根据 Deployment 和环境筛选出需要部署的机器
            var target = await _targetFinder.FindTargetsAsync(deployment, cancellationToken).ConfigureAwait(false);

            if (target == null) throw new InvalidOperationException($"No target machine found for deployment {deployment.Id}");

            Log.Information("Found target machine {MachineName} for deployment {DeploymentId}", target.Name, deployment.Id);

            await InsertMachineVariablesAsync(target, variables, release, plan, cancellationToken).ConfigureAwait(false);

            // 把ExtractCalamariPackage和安装文件流用StartScriptCommand传去目标机器执行
            var extractSuccess = await ExtractCalamariPackageAsync(cancellationToken, extractCalamariPackageScript, calamariPackageBytes, target).ConfigureAwait(false);
            
            if (!extractSuccess)
            {
                throw new InvalidOperationException($"Calamari package extraction failed on one or more target machines for deployment {deployment.Id}");
            }

            Log.Information("Calamari package extraction completed successfully on all target machines for deployment {DeploymentId}", deployment.Id);

            // 后续继续部署

            // 4. 转换ProcessSnapshot为DeploymentStepDto列表
            var steps = ConvertProcessSnapshotToSteps(plan.ProcessSnapshot);

            // 调用 Yaml 相关服务为每个步骤生成对应的 YAML 流
            var yamlStreams = await GenerateYamlStreamsAsync(steps, cancellationToken).ConfigureAwait(false);

            Log.Information("Creating YAML NuGet package for deployment {DeploymentId}", deployment.Id);

            // 把所有 YAML 流打包成一个 NuGet 包的字节数组，准备发送给目标机器
            var yamlNuGetPackageBytes = CreateYamlNuGetPackage(yamlStreams);

            CheckNugetPackage(yamlNuGetPackageBytes);

            // 根据解析后的变量生成 variables.json、sensitiveVariables.json 以及对应的密码
            var (variableJsonStream, sensitiveVariableJsonStream, sensitiveVariablesPassword) = CreateVariableFileStreamsAndPassword(variables);
            
            // 使用 StartScriptCommand 调用 Tentacle，把脚本和 NuGet 包、变量文件一并传到目标机器执行
            var scriptExecution = await StartDeployByCalamariScriptAsync(
                deployByCalamariScript,
                yamlNuGetPackageBytes,
                variableJsonStream,
                sensitiveVariableJsonStream,
                sensitiveVariablesPassword,
                target,
                release.Version,
                cancellationToken).ConfigureAwait(false);

            // 观察远程脚本执行状态和输出日志，并根据结果判断本次部署是否成功
            var executionSuccess = await ObserveDeploymentScriptAsync(scriptExecution, cancellationToken).ConfigureAwait(false);

            // 7. 记录部署完成状态
            await RecordDeploymentCompletionAsync(
                deployment.Id, executionSuccess,
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

    private async Task InsertMachineVariablesAsync(Persistence.Data.Domain.Deployments.Machine target, List<VariableDto> variables, Persistence.Data.Domain.Deployments.Release release, DeploymentPlanDto plan, CancellationToken cancellationToken)
    {
        var kubernetesEndpoint = ParseKubernetesEndpoint(target.Endpoint);

        if (kubernetesEndpoint == null)
        {
            return;
        }

        var accountToken = string.Empty;

        if (!string.IsNullOrEmpty(kubernetesEndpoint.AccountId) && int.TryParse(kubernetesEndpoint.AccountId, out var accountId))
        {
            var account = await _accountDataProvider.GetAccountByIdAsync(accountId, cancellationToken).ConfigureAwait(false);

            if (account != null)
            {
                accountToken = account.Token ?? string.Empty;
            }
        }

        var containerImage = await BuildContainerImageAsync(plan, release, cancellationToken).ConfigureAwait(false);

        variables.AddRange(new List<VariableDto>
        {
            new VariableDto
            {
                Name = "Octopus.Action.Kubernetes.ClusterUrl",
                Value = kubernetesEndpoint.ClusterUrl ?? string.Empty,
                Description = string.Empty,
                Type = VariableType.String,
                IsSensitive = false,
                LastModifiedOn = DateTimeOffset.UtcNow,
                LastModifiedBy = "System"
            },
            new VariableDto
            {
                Name = "Octopus.Account.AccountType",
                Value = "Token",
                Description = string.Empty,
                Type = VariableType.String,
                IsSensitive = false,
                LastModifiedOn = DateTimeOffset.UtcNow,
                LastModifiedBy = "System"
            },
            new VariableDto
            {
                Name = "Octopus.Account.Token",
                Value = accountToken,
                Description = string.Empty,
                Type = VariableType.String,
                IsSensitive = false,
                LastModifiedOn = DateTimeOffset.UtcNow,
                LastModifiedBy = "System"
            },
            new VariableDto
            {
                Name = "Octopus.Action.Kubernetes.SkipTlsVerification",
                Value = kubernetesEndpoint.SkipTlsVerification ?? "False",
                Description = string.Empty,
                Type = VariableType.String,
                IsSensitive = false,
                LastModifiedOn = DateTimeOffset.UtcNow,
                LastModifiedBy = "System"
            },
            new VariableDto
            {
                Name = "Octopus.Action.Script.SuppressEnvironmentLogging",
                Value = "False",
                Description = string.Empty,
                Type = VariableType.String,
                IsSensitive = false,
                LastModifiedOn = DateTimeOffset.UtcNow,
                LastModifiedBy = "System"
            },
            new VariableDto
            {
                Name = "ContainerImage",
                Value = containerImage,
                Description = string.Empty,
                Type = VariableType.String,
                IsSensitive = false,
                LastModifiedOn = DateTimeOffset.UtcNow,
                LastModifiedBy = "System"
            },
            new VariableDto
            {
                Name = "Octopus.Action.Kubernetes.OutputKubectlVersion",
                Value = "True",
                Description = string.Empty,
                Type = VariableType.String,
                IsSensitive = false,
                LastModifiedOn = DateTimeOffset.UtcNow,
                LastModifiedBy = "System"
            },
            new VariableDto
            {
                Name = "OctopusPrintEvaluatedVariables",
                Value = "True",
                Description = string.Empty,
                Type = VariableType.String,
                IsSensitive = false,
                LastModifiedOn = DateTimeOffset.UtcNow,
                LastModifiedBy = "System"
            }
        });
    }

    private async Task<string> BuildContainerImageAsync(DeploymentPlanDto plan, Persistence.Data.Domain.Deployments.Release release, CancellationToken cancellationToken)
    {
        var firstAction = plan.ProcessSnapshot?.StepSnapshots?
            .SelectMany(s => s.Actions)
            .FirstOrDefault(a => a.FeedId.HasValue && !string.IsNullOrEmpty(a.PackageId));

        if (firstAction == null)
        {
            return release.Version;
        }

        var feed = await _externalFeedDataProvider.GetFeedByIdAsync(firstAction.FeedId.Value, cancellationToken).ConfigureAwait(false);

        if (feed == null)
        {
            return release.Version;
        }

        var uri = new Uri(feed.FeedUri ?? string.Empty);

        var feedUri = uri.Host;
        var packageId = firstAction.PackageId ?? string.Empty;
        var version = release.Version ?? string.Empty;

        return $"{feedUri}/{packageId}:{version}";
    }

    public void CheckNugetPackage(byte[] packageBytes)
    {
        using var stream = new MemoryStream(packageBytes);

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Log.Information("=== NuGet Package Contents ===");
        Log.Information("Total entries in package: {Count}", archive.Entries.Count);

        foreach (var entry in archive.Entries)
        {
            Log.Information("Entry: {EntryName}, Size: {Size} bytes", entry.FullName, entry.Length);

            if (entry.FullName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            {
                using var entryStream = entry.Open();

                using var reader = new StreamReader(entryStream);

                var content = reader.ReadToEnd();

                Log.Information("=== {FileName} ===\n{Content}", entry.FullName, content);
            }
        }

        Log.Information("=== End of Package Contents ===");
    }

    private KubernetesEndpointDto ParseKubernetesEndpoint(string endpointJson)
    {
        if (string.IsNullOrEmpty(endpointJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<KubernetesEndpointDto>(endpointJson);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse Kubernetes endpoint from JSON");

            return null;
        }
    }

    private async Task<bool> ExtractCalamariPackageAsync(CancellationToken cancellationToken, string extractCalamariPackageScript, byte[] calamariPackageBytes, Persistence.Data.Domain.Deployments.Machine target)
    {
        var extractExecution = await StartExtractCalamariPackageScriptAsync(
            extractCalamariPackageScript,
            calamariPackageBytes,
            target,
            cancellationToken).ConfigureAwait(false);

        var extractSuccess = await ObserveDeploymentScriptAsync(extractExecution, cancellationToken).ConfigureAwait(false);

        return extractSuccess;
    }

    private async Task<byte[]> DownloadCalamariPackageAsync()
    {
        const string packageId = "Calamari";
        const string githubUserName = "SolarifyDev";

        var version = _calamariGithubPackageSetting.Version;

        var cacheDirectory = string.IsNullOrWhiteSpace(_calamariGithubPackageSetting.CacheDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "CalamariPackages")
            : _calamariGithubPackageSetting.CacheDirectory;

        Directory.CreateDirectory(cacheDirectory);

        var cacheFilePath = Path.Combine(cacheDirectory, $"Calamari.{version}.nupkg");

        if (File.Exists(cacheFilePath))
        {
            Log.Information("Using cached Calamari package from {CachePath}", cacheFilePath);

            return await File.ReadAllBytesAsync(cacheFilePath).ConfigureAwait(false);
        }

        var downloader = new GithubPackageDownloader(githubUserName, _calamariGithubPackageSetting.Token, _calamariGithubPackageSetting.MirrorUrlTemplate);
        var packageStream = await downloader.DownloadPackageAsync(packageId, version).ConfigureAwait(false);

        var bytes = ReadAllBytes(packageStream);

        await File.WriteAllBytesAsync(cacheFilePath, bytes).ConfigureAwait(false);

        Log.Information("Downloaded and cached Calamari package to {CachePath}", cacheFilePath);

        return bytes;
    }

    private async Task<(Persistence.Data.Domain.Deployments.Machine Machine, IAsyncScriptService ScriptClient, ScriptTicket Ticket)> StartDeployByCalamariScriptAsync(
        string deployByCalamariScript,
        byte[] yamlNuGetPackageBytes,
        Stream variableJsonStream,
        Stream sensitiveVariableJsonStream,
        string sensitiveVariablesPassword,
        Persistence.Data.Domain.Deployments.Machine target,
        string version,
        CancellationToken cancellationToken)
    {
        if (target == null)
        {
            throw new InvalidOperationException("No target machine to execute DeployByCalamari script");
        }

        var packageBytes = yamlNuGetPackageBytes ?? Array.Empty<byte>();
        var variableBytes = ReadAllBytes(variableJsonStream);
        var sensitiveBytes = ReadAllBytes(sensitiveVariableJsonStream);

        var packageFileName = $"squid.{version}.nupkg";
        const string variableFileName = "variables.json";
        const string sensitiveVariableFileName = "sensitiveVariables.json";

        var packageFilePath = $".\\{packageFileName}";
        var variableFilePath = $".\\{variableFileName}";
        var sensitiveVariableFilePath = string.IsNullOrEmpty(sensitiveVariablesPassword) ? string.Empty : $".\\{sensitiveVariableFileName}";

        var calamariVersion = GetCalamariVersion();

        var scriptBody = deployByCalamariScript
            .Replace("{{PackageFilePath}}", packageFilePath, StringComparison.Ordinal)
            .Replace("{{VariableFilePath}}", variableFilePath, StringComparison.Ordinal)
            .Replace("{{SensitiveVariableFile}}", sensitiveVariableFilePath, StringComparison.Ordinal)
            .Replace("{{SensitiveVariablePassword}}", sensitiveVariablesPassword, StringComparison.Ordinal)
            .Replace("{{CalamariVersion}}", GetCalamariVersion() + $"/{target.OperatingSystem.GetDescription()}", StringComparison.Ordinal);

        cancellationToken.ThrowIfCancellationRequested();

        var endpoint = ParseMachineEndpoint(target);

        if (endpoint == null)
        {
            throw new InvalidOperationException($"Endpoint could not be parsed for machine {target.Name}");
        }

        var scriptFiles = new[]
        {
            new ScriptFile(packageFileName, DataStream.FromBytes(packageBytes), null),
            new ScriptFile(variableFileName, DataStream.FromBytes(variableBytes), null),
            new ScriptFile(sensitiveVariableFileName, DataStream.FromBytes(sensitiveBytes), sensitiveVariablesPassword)
        };

        Log.Information("Starting DeployByCalamari with script {@Script}", scriptBody);

        var command = new StartScriptCommand(
            scriptBody,
            ScriptIsolationLevel.FullIsolation,
            TimeSpan.FromMinutes(30),
            null,
            Array.Empty<string>(),
            null,
            scriptFiles);

        var scriptClient = _halibutRuntime.CreateAsyncClient<IScriptService, IAsyncScriptService>(endpoint);

        var ticket = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

        Log.Information("Starting DeployByCalamari script on machine {MachineName} with ticket id: {Tiecket}", target.Name, ticket);

        return (target, scriptClient, ticket);
    }

    private async Task<(Persistence.Data.Domain.Deployments.Machine Machine, IAsyncScriptService ScriptClient, ScriptTicket Ticket)> StartExtractCalamariPackageScriptAsync(
        string extractCalamariPackageScript,
        byte[] calamariPackageBytes,
        Persistence.Data.Domain.Deployments.Machine target,
        CancellationToken cancellationToken)
    {
        if (target == null)
        {
            throw new InvalidOperationException("No target machine to execute ExtractCalamariPackage script");
        }

        var calamariVersion = _calamariGithubPackageSetting.Version;

        var scriptBody = extractCalamariPackageScript
            .Replace("{{CalamariPath}}", string.Empty, StringComparison.Ordinal)
            .Replace("{{CalamariPackageVersion}}", calamariVersion, StringComparison.Ordinal)
            .Replace("{{CalamariPackage}}", "SolarifyDev.SquidCalamari", StringComparison.Ordinal)
            .Replace("{{SupportPackage}}", string.Empty, StringComparison.Ordinal)
            .Replace("{{SupportPackageVersion}}", string.Empty, StringComparison.Ordinal)
            .Replace("{{UsesCustomPackageDirectory}}", "false", StringComparison.Ordinal)
            .Replace("{{CalamariPackageVersionsToKeep}}", calamariVersion, StringComparison.Ordinal);

        var calamariPackageFileName = $"SolarifyDev.SquidCalamari.{calamariVersion}.nupkg";

        cancellationToken.ThrowIfCancellationRequested();

        var endpoint = ParseMachineEndpoint(target);

        if (endpoint == null)
        {
            throw new InvalidOperationException($"Endpoint could not be parsed for machine {target.Name}");
        }

        var scriptFiles = new[]
        {
            new ScriptFile(calamariPackageFileName, DataStream.FromBytes(calamariPackageBytes), null)
        };

        var command = new StartScriptCommand(
            scriptBody,
            ScriptIsolationLevel.FullIsolation,
            TimeSpan.FromMinutes(30),
            null,
            Array.Empty<string>(),
            null,
            scriptFiles);

        var scriptClient = _halibutRuntime.CreateAsyncClient<IScriptService, IAsyncScriptService>(endpoint);

        var ticket = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

        Log.Information("Starting ExtractCalamariPackage script on machine {MachineName} with ticket id: {Ticket}", target.Name, ticket);

        return (target, scriptClient, ticket);
    }

    private async Task<bool> ObserveDeploymentScriptAsync(
        (Persistence.Data.Domain.Deployments.Machine Machine, IAsyncScriptService ScriptClient, ScriptTicket Ticket) execution,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scriptTimeout = timeout ?? TimeSpan.FromMinutes(3);
        var startTime = DateTime.UtcNow;

        var scriptStatusResponse = new ScriptStatusResponse(
            execution.Ticket,
            ProcessState.Pending,
            0,
            new List<ProcessOutput>(),
            0);

        var logs = new List<ProcessOutput>();

        try
        {
            while (scriptStatusResponse.State != ProcessState.Complete)
            {
                if (DateTime.UtcNow - startTime > scriptTimeout)
                {
                    Log.Warning(
                        "Script execution timeout on machine {MachineName}, cancelling script with ticket {Ticket}",
                        execution.Machine.Name,
                        execution.Ticket.TaskId);

                    await CancelScriptAsync(execution, scriptStatusResponse.NextLogSequence).ConfigureAwait(false);

                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();

                scriptStatusResponse = await execution.ScriptClient.GetStatusAsync(
                    new ScriptStatusRequest(execution.Ticket, scriptStatusResponse.NextLogSequence)).ConfigureAwait(false);

                logs.AddRange(scriptStatusResponse.Logs);

                foreach (var log in scriptStatusResponse.Logs)
                {
                    Log.Information(
                        "[Deployment Script] Machine={MachineName}, Time={Time}, Source={Source}, Message={Message}",
                        execution.Machine.Name,
                        log.Occurred,
                        log.Source,
                        log.Text);
                }

                if (scriptStatusResponse.State != ProcessState.Complete)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warning(
                "Script observation cancelled on machine {MachineName}, cancelling script with ticket {Ticket}",
                execution.Machine.Name,
                execution.Ticket.TaskId);

            await CancelScriptAsync(execution, scriptStatusResponse.NextLogSequence).ConfigureAwait(false);

            throw;
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

        return success;
    }

    private async Task CancelScriptAsync(
        (Persistence.Data.Domain.Deployments.Machine Machine, IAsyncScriptService ScriptClient, ScriptTicket Ticket) execution,
        long lastLogSequence)
    {
        try
        {
            var cancelResponse = await execution.ScriptClient.CancelScriptAsync(
                new CancelScriptCommand(execution.Ticket, lastLogSequence)).ConfigureAwait(false);

            Log.Information(
                "Script cancelled on machine {MachineName}, ticket {Ticket}, state: {State}",
                execution.Machine.Name,
                execution.Ticket.TaskId,
                cancelResponse.State);
        }
        catch (Exception ex)
        {
            Log.Error(
                ex,
                "Failed to cancel script on machine {MachineName}, ticket {Ticket}",
                execution.Machine.Name,
                execution.Ticket.TaskId);
        }
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

    private string GetCalamariVersion()
    {
        return string.IsNullOrWhiteSpace(_calamariGithubPackageSetting.Version) ? "28.2.1" : _calamariGithubPackageSetting.Version;
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

    private ServiceEndPoint? ParseMachineEndpoint(Persistence.Data.Domain.Deployments.Machine machine)
    {
        try
        {
            if (string.IsNullOrEmpty(machine.Uri) || string.IsNullOrEmpty(machine.Thumbprint))
            {
                Log.Warning("Machine {MachineName} has missing Uri or Thumbprint", machine.Name);

                return null;
            }

            return new ServiceEndPoint(machine.Uri, machine.Thumbprint, HalibutTimeoutsAndLimits.RecommendedValues());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse machine endpoint for machine {MachineName}", machine.Name);

            return null;
        }
    }

    private async Task<Dictionary<string, Stream>> GenerateYamlStreamsAsync(
        List<DeploymentStepDto> steps,
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

    private List<DeploymentStepDto> ConvertProcessSnapshotToSteps(
        ProcessSnapshotData processSnapshot)
    {
        var steps = new List<DeploymentStepDto>();

        foreach (var stepSnap in processSnapshot.StepSnapshots.OrderBy(p => p.StepOrder))
        {
            var step = new DeploymentStepDto
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
                Properties = stepSnap.Properties.Select(
                    kvp =>
                        new DeploymentStepPropertyDto
                        {
                            StepId = stepSnap.Id, PropertyName = kvp.Key, PropertyValue = kvp.Value
                        }).ToList(),
                Actions = stepSnap.Actions.Select(
                    action =>
                        new DeploymentActionDto
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
                            Properties = action.Properties.Select(
                                kvp =>
                                    new DeploymentActionPropertyDto
                                    {
                                        ActionId = action.Id, PropertyName = kvp.Key, PropertyValue = kvp.Value
                                    }).ToList()
                        }).ToList()
            };

            steps.Add(step);
        }

        return steps;
    }

    private (Stream variableJsonStream, Stream sensitiveVariableJsonStream, string password) CreateVariableFileStreamsAndPassword(
        List<VariableDto> variables)
    {
        if (variables == null || variables.Count == 0)
        {
            var emptyJson = "{}";
            var emptyBytes = Encoding.UTF8.GetBytes(emptyJson);

            return (new MemoryStream(emptyBytes), new MemoryStream(emptyBytes), string.Empty);
        }

        var nonSensitiveVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sensitiveVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var variable in variables)
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
            var encryptedBytes = encryption.Encrypt(sensitiveJson); // 加密整个JSON

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

        var completion = new Persistence.Data.Domain.Deployments.DeploymentCompletion
        {
            DeploymentId = deploymentId,
            CompletedTime = DateTimeOffset.UtcNow,
            State = success ? "Success" : "Failed",
            SpaceId = deployment?.SpaceId ?? 1, // 如果找不到部署，使用默认SpaceId
            SequenceNumber = 0 // 这个字段会由数据库自动生成
        };

        await _deploymentCompletionDataProvider.AddDeploymentCompletionAsync(completion).ConfigureAwait(false);

        Log.Information(
            "Recorded deployment completion for deployment {DeploymentId} (Release {ReleaseId}): {Status}",
            deploymentId, deployment?.ReleaseId, success ? "Success" : "Failed");
    }
}