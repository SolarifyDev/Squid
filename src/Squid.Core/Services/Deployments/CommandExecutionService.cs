using System.Diagnostics;
using System.Text.Json;
using Halibut;
using Halibut.ServiceModel;
using Squid.Core.Commands.Tentacle;
using Squid.Core.Services.Tentacle;
using Squid.Message.Domain.Deployments;

namespace Squid.Core.Services.Deployments;
//
public class CommandExecutionService : ICommandExecutionService
{
//     private readonly HalibutRuntime _halibutRuntime;
//
//     public CommandExecutionService(HalibutRuntime halibutRuntime)
//     {
//         _halibutRuntime = halibutRuntime;
//     }
//
     public async Task<CommandExecutionResult> ExecuteCommandAsync(ActionCommand command, Message.Domain.Deployments.Machine targetMachine, CancellationToken cancellationToken = default)
     {
          return new CommandExecutionResult();
//         var stopwatch = Stopwatch.StartNew();
//         
//         try
//         {
//             Log.Information("Executing command {CommandText} on machine {MachineName} ({MachineId})", 
//                 command.CommandText, targetMachine.Name, targetMachine.Id);
//
//             // 解析机器的连接信息
//             var machineEndpoint = ParseMachineEndpoint(targetMachine);
//             if (machineEndpoint == null)
//             {
//                 return new CommandExecutionResult
//                 {
//                     Success = false,
//                     Error = $"Unable to parse machine endpoint for machine {targetMachine.Name}",
//                     Duration = stopwatch.Elapsed
//                 };
//             }
//
//             // 根据命令类型执行不同的逻辑
//             var result = await ExecuteCommandByTypeAsync(command, machineEndpoint, cancellationToken).ConfigureAwait(false);
//             
//             result.Duration = stopwatch.Elapsed;
//             
//             Log.Information("Command execution completed for {CommandText} on machine {MachineName}. Success: {Success}, Duration: {Duration}ms", 
//                 command.CommandText, targetMachine.Name, result.Success, result.Duration.TotalMilliseconds);
//
//             return result;
//         }
//         catch (Exception ex)
//         {
//             Log.Error(ex, "Failed to execute command {CommandText} on machine {MachineName}", 
//                 command.CommandText, targetMachine.Name);
//
//             return new CommandExecutionResult
//             {
//                 Success = false,
//                 Error = ex.Message,
//                 Duration = stopwatch.Elapsed
//             };
//         }
     }
//
//     private ServiceEndPoint? ParseMachineEndpoint(Message.Domain.Deployments.Machine machine)
//     {
//         try
//         {
//             if (string.IsNullOrEmpty(machine.Json))
//                 return null;
//
//             var machineConfig = JsonSerializer.Deserialize<MachineConfiguration>(machine.Json);
//             if (machineConfig?.Endpoint == null)
//                 return null;
//
//             return new ServiceEndPoint(machineConfig.Endpoint.Uri, machineConfig.Endpoint.Thumbprint);
//         }
//         catch (Exception ex)
//         {
//             Log.Warning(ex, "Failed to parse machine endpoint for machine {MachineName}", machine.Name);
//             return null;
//         }
//     }
//
//     private async Task<CommandExecutionResult> ExecuteCommandByTypeAsync(ActionCommand command, ServiceEndPoint endpoint, CancellationToken cancellationToken)
//     {
//         // 解析命令类型
//         var commandText = command.CommandText.ToLower();
//
//         if (commandText.StartsWith("execute-script"))
//         {
//             return await ExecuteScriptCommandAsync(command, endpoint, cancellationToken).ConfigureAwait(false);
//         }
//         else if (commandText.StartsWith("deploy-package"))
//         {
//             return await ExecutePackageCommandAsync(command, endpoint, cancellationToken).ConfigureAwait(false);
//         }
//         else if (commandText.StartsWith("kubectl-apply"))
//         {
//             return await ExecuteKubernetesCommandAsync(command, endpoint, cancellationToken).ConfigureAwait(false);
//         }
//         else if (commandText.StartsWith("http-request"))
//         {
//             return await ExecuteHttpRequestCommandAsync(command, endpoint, cancellationToken).ConfigureAwait(false);
//         }
//         else if (commandText.StartsWith("manual-intervention"))
//         {
//             return await ExecuteManualCommandAsync(command, endpoint, cancellationToken).ConfigureAwait(false);
//         }
//         else if (commandText.StartsWith("deploy-release"))
//         {
//             return await ExecuteDeployReleaseCommandAsync(command, endpoint, cancellationToken).ConfigureAwait(false);
//         }
//         else if (commandText.StartsWith("kubectl-ingress"))
//         {
//             return await ExecuteIngressCommandAsync(command, endpoint, cancellationToken).ConfigureAwait(false);
//         }
//         else if (commandText.StartsWith("powershell"))
//         {
//             return await ExecutePowerShellCommandAsync(command, endpoint, cancellationToken).ConfigureAwait(false);
//         }
//         else if (commandText.StartsWith("bash"))
//         {
//             return await ExecuteBashCommandAsync(command, endpoint, cancellationToken).ConfigureAwait(false);
//         }
//         else
//         {
//             return await ExecuteGenericCommandAsync(command, endpoint, cancellationToken).ConfigureAwait(false);
//         }
//     }
//
//     private async Task<CommandExecutionResult> ExecuteScriptCommandAsync(ActionCommand command, ServiceEndPoint endpoint, CancellationToken cancellationToken)
//     {
//         try
//         {
//             var scriptBody = command.Parameters.GetValueOrDefault("ScriptBody", "echo 'No script body'");
//             
//             var startCommand = new StartScriptCommand(
//                 scriptBody,
//                 ScriptIsolationLevel.NoIsolation,
//                 TimeSpan.FromMinutes(30),
//                 null,
//                 Array.Empty<string>(),
//                 command.ActionId.ToString()
//             );
//
//             var scriptService = _halibutRuntime.CreateAsyncClient<IAsyncScriptService>(endpoint);
//             
//             // 启动脚本
//             var ticket = await scriptService.StartScriptAsync(startCommand).ConfigureAwait(false);
//             
//             // 等待脚本完成
//             var result = await WaitForScriptCompletionAsync(scriptService, ticket, cancellationToken).ConfigureAwait(false);
//             
//             return result;
//         }
//         catch (Exception ex)
//         {
//             return new CommandExecutionResult
//             {
//                 Success = false,
//                 Error = $"Script execution failed: {ex.Message}"
//             };
//         }
//     }
//
//     private async Task<CommandExecutionResult> ExecutePackageCommandAsync(ActionCommand command, ServiceEndPoint endpoint, CancellationToken cancellationToken)
//     {
//         // 包部署逻辑 - 这里简化实现
//         var packageId = command.Parameters.GetValueOrDefault("PackageId", "unknown");
//         var version = command.Parameters.GetValueOrDefault("Version", "latest");
//         
//         Log.Information("Deploying package {PackageId} version {Version}", packageId, version);
//         
//         // 实际实现中需要：
//         // 1. 下载包文件
//         // 2. 传输到目标机器
//         // 3. 执行部署脚本
//         
//         await Task.Delay(1000, cancellationToken).ConfigureAwait(false); // 模拟部署时间
//         
//         return new CommandExecutionResult
//         {
//             Success = true,
//             Output = $"Package {packageId} version {version} deployed successfully"
//         };
//     }
//
//     private async Task<CommandExecutionResult> ExecutePowerShellCommandAsync(ActionCommand command, ServiceEndPoint endpoint, CancellationToken cancellationToken)
//     {
//         var script = command.Parameters.GetValueOrDefault("PowerShellScript", "Write-Host 'No script'");
//         
//         var startCommand = new StartScriptCommand(
//             script,
//             ScriptIsolationLevel.NoIsolation,
//             TimeSpan.FromMinutes(30),
//             null,
//             Array.Empty<string>(),
//             command.ActionId.ToString()
//         );
//
//         var scriptService = _halibutRuntime.CreateAsyncClient<IAsyncScriptService>(endpoint);
//         var ticket = await scriptService.StartScriptAsync(startCommand).ConfigureAwait(false);
//         
//         return await WaitForScriptCompletionAsync(scriptService, ticket, cancellationToken).ConfigureAwait(false);
//     }
//
//     private async Task<CommandExecutionResult> ExecuteBashCommandAsync(ActionCommand command, ServiceEndPoint endpoint, CancellationToken cancellationToken)
//     {
//         var script = command.Parameters.GetValueOrDefault("BashScript", "echo 'No script'");
//         
//         var startCommand = new StartScriptCommand(
//             script,
//             ScriptIsolationLevel.NoIsolation,
//             TimeSpan.FromMinutes(30),
//             null,
//             Array.Empty<string>(),
//             command.ActionId.ToString()
//         );
//
//         var scriptService = _halibutRuntime.CreateAsyncClient<IAsyncScriptService>(endpoint);
//         var ticket = await scriptService.StartScriptAsync(startCommand).ConfigureAwait(false);
//         
//         return await WaitForScriptCompletionAsync(scriptService, ticket, cancellationToken).ConfigureAwait(false);
//     }
//
//     private async Task<CommandExecutionResult> ExecuteGenericCommandAsync(ActionCommand command, ServiceEndPoint endpoint, CancellationToken cancellationToken)
//     {
//         // 通用命令执行 - 将命令作为脚本执行
//         var startCommand = new StartScriptCommand(
//             command.CommandText,
//             ScriptIsolationLevel.NoIsolation,
//             TimeSpan.FromMinutes(30),
//             null,
//             Array.Empty<string>(),
//             command.ActionId.ToString()
//         );
//
//         var scriptService = _halibutRuntime.CreateAsyncClient<IAsyncScriptService>(endpoint);
//         var ticket = await scriptService.StartScriptAsync(startCommand).ConfigureAwait(false);
//         
//         return await WaitForScriptCompletionAsync(scriptService, ticket, cancellationToken).ConfigureAwait(false);
//     }
//
//     private async Task<CommandExecutionResult> ExecuteKubernetesCommandAsync(ActionCommand command, ServiceEndPoint endpoint, CancellationToken cancellationToken)
//     {
//         try
//         {
//             var yaml = command.Parameters.GetValueOrDefault("KubernetesYaml", "");
//             var namespace_ = command.Parameters.GetValueOrDefault("KubernetesNamespace", "default");
//
//             Log.Information("Executing Kubernetes deployment in namespace {Namespace}", namespace_);
//
//             // 构建kubectl命令脚本
//             var kubectlScript = $@"
// echo 'Applying Kubernetes YAML to namespace {namespace_}...'
// kubectl apply -f - --namespace={namespace_} <<EOF
// {yaml}
// EOF
// echo 'Kubernetes deployment completed'
// ";
//
//             var startCommand = new StartScriptCommand(
//                 kubectlScript,
//                 ScriptIsolationLevel.NoIsolation,
//                 TimeSpan.FromMinutes(30),
//                 null,
//                 Array.Empty<string>(),
//                 command.ActionId.ToString()
//             );
//
//             var scriptService = _halibutRuntime.CreateAsyncClient<IAsyncScriptService>(endpoint);
//             var ticket = await scriptService.StartScriptAsync(startCommand).ConfigureAwait(false);
//
//             return await WaitForScriptCompletionAsync(scriptService, ticket, cancellationToken).ConfigureAwait(false);
//         }
//         catch (Exception ex)
//         {
//             return new CommandExecutionResult
//             {
//                 Success = false,
//                 Error = $"Kubernetes deployment failed: {ex.Message}"
//             };
//         }
//     }
//
//     private async Task<CommandExecutionResult> ExecuteHttpRequestCommandAsync(ActionCommand command, ServiceEndPoint endpoint, CancellationToken cancellationToken)
//     {
//         try
//         {
//             var url = command.Parameters.GetValueOrDefault("HttpUrl", "");
//             var method = command.Parameters.GetValueOrDefault("HttpMethod", "GET");
//             var headers = command.Parameters.GetValueOrDefault("HttpHeaders", "");
//             var body = command.Parameters.GetValueOrDefault("HttpBody", "");
//
//             Log.Information("Executing HTTP {Method} request to {Url}", method, url);
//
//             // 构建HTTP请求脚本（使用PowerShell的Invoke-RestMethod）
//             var httpScript = $@"
// $url = '{url}'
// $method = '{method}'
// $headers = @{{}}
//
// # 解析headers
// if ('{headers}' -ne '') {{
//     $headerLines = '{headers}' -split ';'
//     foreach ($line in $headerLines) {{
//         $parts = $line -split ':', 2
//         if ($parts.Length -eq 2) {{
//             $headers[$parts[0].Trim()] = $parts[1].Trim()
//         }}
//     }}
// }}
//
// try {{
//     if ('{body}' -ne '' -and $method -in @('POST', 'PUT', 'PATCH')) {{
//         $response = Invoke-RestMethod -Uri $url -Method $method -Headers $headers -Body '{body}' -ContentType 'application/json'
//     }} else {{
//         $response = Invoke-RestMethod -Uri $url -Method $method -Headers $headers
//     }}
//
//     Write-Host ""HTTP request completed successfully""
//     Write-Host ""Response: $($response | ConvertTo-Json -Depth 3)""
// }} catch {{
//     Write-Error ""HTTP request failed: $($_.Exception.Message)""
//     exit 1
// }}
// ";
//
//             var startCommand = new StartScriptCommand(
//                 httpScript,
//                 ScriptIsolationLevel.NoIsolation,
//                 TimeSpan.FromMinutes(10),
//                 null,
//                 Array.Empty<string>(),
//                 command.ActionId.ToString()
//             );
//
//             var scriptService = _halibutRuntime.CreateAsyncClient<IAsyncScriptService>(endpoint);
//             var ticket = await scriptService.StartScriptAsync(startCommand).ConfigureAwait(false);
//
//             return await WaitForScriptCompletionAsync(scriptService, ticket, cancellationToken).ConfigureAwait(false);
//         }
//         catch (Exception ex)
//         {
//             return new CommandExecutionResult
//             {
//                 Success = false,
//                 Error = $"HTTP request failed: {ex.Message}"
//             };
//         }
//     }
//
//     private async Task<CommandExecutionResult> ExecuteManualCommandAsync(ActionCommand command, ServiceEndPoint endpoint, CancellationToken cancellationToken)
//     {
//         // 手动干预不需要在目标机器上执行，而是需要在部署系统中创建一个等待状态
//         var instructions = command.Parameters.GetValueOrDefault("ManualInstructions", "Manual intervention required");
//         var responsibleTeams = command.Parameters.GetValueOrDefault("ManualResponsibleTeamIds", "");
//
//         Log.Information("Manual intervention required: {Instructions}", instructions);
//
//         // 这里应该创建一个手动干预任务，等待用户确认
//         // 目前简化为自动通过
//         await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
//
//         return new CommandExecutionResult
//         {
//             Success = true,
//             Output = $"Manual intervention completed: {instructions}"
//         };
//     }
//
//     private async Task<CommandExecutionResult> ExecuteDeployReleaseCommandAsync(ActionCommand command, ServiceEndPoint endpoint, CancellationToken cancellationToken)
//     {
//         // 部署Release是一个复杂的操作，需要递归调用部署系统
//         var projectId = command.Parameters.GetValueOrDefault("DeployReleaseProjectId", "");
//         var version = command.Parameters.GetValueOrDefault("DeployReleaseVersion", "latest");
//         var channelId = command.Parameters.GetValueOrDefault("DeployReleaseChannelId", "");
//
//         Log.Information("Deploying release {Version} of project {ProjectId} via channel {ChannelId}",
//             version, projectId, channelId);
//
//         // 这里应该触发一个新的部署任务
//         // 目前简化为模拟成功
//         await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
//
//         return new CommandExecutionResult
//         {
//             Success = true,
//             Output = $"Release {version} of project {projectId} deployed successfully"
//         };
//     }
//
//     private async Task<CommandExecutionResult> ExecuteIngressCommandAsync(ActionCommand command, ServiceEndPoint endpoint, CancellationToken cancellationToken)
//     {
//         try
//         {
//             var ingressName = command.Parameters.GetValueOrDefault("IngressName", "default-ingress");
//             var namespace_ = command.Parameters.GetValueOrDefault("KubernetesNamespace", "default");
//
//             Log.Information("Deploying ingress {IngressName} in namespace {Namespace}", ingressName, namespace_);
//
//             // 构建kubectl ingress命令脚本
//             var ingressScript = $@"
// echo 'Deploying ingress {ingressName} to namespace {namespace_}...'
// kubectl get ingress {ingressName} --namespace={namespace_} || echo 'Ingress not found, will be created'
// # 这里应该有具体的ingress配置和部署逻辑
// echo 'Ingress deployment completed'
// ";
//
//             var startCommand = new StartScriptCommand(
//                 ingressScript,
//                 ScriptIsolationLevel.NoIsolation,
//                 TimeSpan.FromMinutes(10),
//                 null,
//                 Array.Empty<string>(),
//                 command.ActionId.ToString()
//             );
//
//             var scriptService = _halibutRuntime.CreateAsyncClient<IAsyncScriptService>(endpoint);
//             var ticket = await scriptService.StartScriptAsync(startCommand).ConfigureAwait(false);
//
//             return await WaitForScriptCompletionAsync(scriptService, ticket, cancellationToken).ConfigureAwait(false);
//         }
//         catch (Exception ex)
//         {
//             return new CommandExecutionResult
//             {
//                 Success = false,
//                 Error = $"Ingress deployment failed: {ex.Message}"
//             };
//         }
//     }
//
//     private async Task<CommandExecutionResult> WaitForScriptCompletionAsync(IAsyncScriptService scriptService, ScriptTicket ticket, CancellationToken cancellationToken)
//     {
//         var lastLogSequence = 0L;
//         var allLogs = new List<string>();
//         
//         while (!cancellationToken.IsCancellationRequested)
//         {
//             var statusRequest = new ScriptStatusRequest(ticket, lastLogSequence);
//             var status = await scriptService.GetStatusAsync(statusRequest).ConfigureAwait(false);
//             
//             // 收集日志
//             foreach (var log in status.Logs)
//             {
//                 allLogs.Add($"[{log.Occurred:yyyy-MM-dd HH:mm:ss}] {log.Text}");
//             }
//             
//             lastLogSequence = status.NextLogSequence;
//             
//             if (status.State == ProcessState.Complete)
//             {
//                 // 完成脚本
//                 await scriptService.CompleteScriptAsync(new CompleteScriptCommand(ticket)).ConfigureAwait(false);
//                 
//                 return new CommandExecutionResult
//                 {
//                     Success = status.ExitCode == 0,
//                     Output = string.Join(Environment.NewLine, allLogs),
//                     ExitCode = status.ExitCode
//                 };
//             }
//             
//             if (status.State == ProcessState.Crashed)
//             {
//                 return new CommandExecutionResult
//                 {
//                     Success = false,
//                     Error = "Script crashed",
//                     Output = string.Join(Environment.NewLine, allLogs),
//                     ExitCode = status.ExitCode
//                 };
//             }
//             
//             // 等待一段时间再检查状态
//             await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
//         }
//         
//         return new CommandExecutionResult
//         {
//             Success = false,
//             Error = "Script execution was cancelled",
//             Output = string.Join(Environment.NewLine, allLogs)
//         };
//     }
}
//
// // 机器配置数据结构
// public class MachineConfiguration
// {
//     public MachineEndpoint? Endpoint { get; set; }
// }
//
// public class MachineEndpoint
// {
//     public string Uri { get; set; } = string.Empty;
//     public string Thumbprint { get; set; } = string.Empty;
// }
