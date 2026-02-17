using Halibut;
using System.Text;
using System.Text.Json;
using Squid.Core.Commands.Tentacle;
using Squid.Core.Services.Common;
using Squid.Core.Services.Tentacle;
using Squid.Core.VariableSubstitution;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments;

public partial class DeploymentTaskExecutor
{
    private async Task PrepareAndExecuteStepsAsync(long? taskActivityNodeId, CancellationToken ct)
    {
        var targetRoles = DeploymentTargetFinder.ParseRoles(_ctx.Target.Roles);
        var failureEncountered = false;
        var variableDictionary = VariableDictionaryFactory.Create(_ctx.Variables);
        var stepSortOrder = 0;

        var orderedSteps = _ctx.Steps.OrderBy(p => p.StepOrder).ToList();
        var batches = StepBatcher.BatchSteps(orderedSteps);

        foreach (var batch in batches)
        {
            if (batch.Count == 1)
            {
                var step = batch[0];
                var result = await ExecuteStepAsync(step, variableDictionary, targetRoles, failureEncountered,
                        taskActivityNodeId, ++stepSortOrder, ct)
                    .ConfigureAwait(false);
                failureEncountered = MergeStepResult(result, failureEncountered);
            }
            else
            {
                var tasks = batch.Select(step =>
                    ExecuteStepAsync(step, variableDictionary, targetRoles, failureEncountered,
                        taskActivityNodeId, ++stepSortOrder, ct)).ToList();
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var result in results)
                    failureEncountered = MergeStepResult(result, failureEncountered);
            }

            variableDictionary = VariableDictionaryFactory.Create(_ctx.Variables);
        }
    }

    private async Task<StepExecutionResult> ExecuteStepAsync(
        DeploymentStepDto step,
        VariableDictionary variableDictionary,
        HashSet<string> targetRoles,
        bool failureEncountered,
        long? parentActivityNodeId,
        int stepSortOrder,
        CancellationToken ct)
    {
        var result = new StepExecutionResult();

        if (!ShouldExecuteStep(step, targetRoles, previousStepSucceeded: !failureEncountered))
        {
            Log.Information("Skipping step {StepName} (disabled, condition, or role mismatch)", step.Name);
            return result;
        }

        var stepActivityNode = await CreateActivityNodeAsync(
            _ctx.Task.Id, parentActivityNodeId, step.Name, "Step", "Running", stepSortOrder, ct)
            .ConfigureAwait(false);

        var stepResults = new List<ActionExecutionResult>();
        var actionSortOrder = 0;

        foreach (var action in step.Actions.OrderBy(p => p.ActionOrder))
        {
            if (!ShouldExecuteAction(action, _ctx.Deployment.EnvironmentId, _ctx.Deployment.ChannelId))
            {
                Log.Information("Skipping action {ActionName} (disabled, environment, or channel mismatch)", action.Name);
                continue;
            }

            var handler = _actionHandlerRegistry.Resolve(action);

            if (handler == null)
            {
                Log.Warning("No handler found for action {ActionType}, skipping", action.ActionType);
                continue;
            }

            var expandedAction = VariableExpander.ExpandActionProperties(action, variableDictionary);

            var context = new ActionExecutionContext
            {
                Step = step,
                Action = expandedAction,
                Variables = _ctx.Variables,
                ReleaseVersion = _ctx.Release?.Version
            };

            var prepared = await handler.PrepareAsync(context, ct).ConfigureAwait(false);

            if (prepared != null)
            {
                if (prepared.ScriptBody != null)
                    prepared.ScriptBody = VariableExpander.ExpandString(prepared.ScriptBody, variableDictionary);

                if (prepared.CalamariCommand == null)
                {
                    var wrapper = _scriptWrappers.FirstOrDefault(
                        w => w.CanWrap(_ctx.CommunicationStyle));

                    if (wrapper != null)
                    {
                        prepared.ScriptBody = wrapper.WrapScript(
                            prepared.ScriptBody, _ctx.EndpointJson, _ctx.Account,
                            prepared.Syntax, _ctx.Variables);
                    }
                }

                stepResults.Add(prepared);
            }
        }

        foreach (var actionResult in stepResults)
        {
            var actionActivityNode = await CreateActivityNodeAsync(
                _ctx.Task.Id, stepActivityNode?.Id, actionResult.CalamariCommand ?? "Direct Script",
                "Action", "Running", ++actionSortOrder, ct).ConfigureAwait(false);

            try
            {
                if (actionResult.CalamariCommand != null)
                    await ExecuteCalamariActionAsync(actionResult, ct).ConfigureAwait(false);
                else
                    await ExecuteDirectScriptAsync(actionResult, ct).ConfigureAwait(false);

                result.ActionResults.Add(actionResult);

                if (actionActivityNode != null)
                    await _activityLogDataProvider.UpdateNodeStatusAsync(
                        actionActivityNode.Id, "Success", DateTimeOffset.UtcNow, ct: ct).ConfigureAwait(false);

                foreach (var kv in actionResult.OutputVariables)
                {
                    var qualifiedName = $"Octopus.Action[{step.Name}].Output.{kv.Key}";
                    result.OutputVariables.Add(new VariableDto { Name = qualifiedName, Value = kv.Value });
                    result.OutputVariables.Add(new VariableDto { Name = kv.Key, Value = kv.Value });
                }
            }
            catch (Exception ex)
            {
                result.Failed = true;
                Log.Error(ex, "Action failed in step {StepName}: {Error}", step.Name, ex.Message);

                if (actionActivityNode != null)
                    await _activityLogDataProvider.UpdateNodeStatusAsync(
                        actionActivityNode.Id, "Failed", DateTimeOffset.UtcNow, ct: ct).ConfigureAwait(false);

                if (step.IsRequired)
                    throw;
            }
        }

        if (stepActivityNode != null)
            await _activityLogDataProvider.UpdateNodeStatusAsync(
                stepActivityNode.Id, result.Failed ? "Failed" : "Success", DateTimeOffset.UtcNow, ct: ct)
                .ConfigureAwait(false);

        return result;
    }

    private bool MergeStepResult(StepExecutionResult result, bool failureEncountered)
    {
        _ctx.ActionResults.AddRange(result.ActionResults);

        if (result.OutputVariables.Count > 0)
        {
            _ctx.Variables.AddRange(result.OutputVariables);
            foreach (var v in result.OutputVariables)
                Log.Information("Output variable captured: {Name} = {Value}", v.Name, v.Value);
        }

        return failureEncountered || result.Failed;
    }

    private class StepExecutionResult
    {
        public List<ActionExecutionResult> ActionResults { get; } = new();
        public List<VariableDto> OutputVariables { get; } = new();
        public bool Failed { get; set; }
    }

    public static bool ShouldExecuteStep(
        DeploymentStepDto step,
        HashSet<string> targetRoles,
        bool previousStepSucceeded)
    {
        if (step.IsDisabled)
            return false;

        if (!EvaluateCondition(step.Condition, previousStepSucceeded))
            return false;

        if (!MatchesTargetRoles(step, targetRoles))
            return false;

        return true;
    }

    public static bool ShouldExecuteAction(
        DeploymentActionDto action,
        int deploymentEnvironmentId,
        int deploymentChannelId)
    {
        if (action.IsDisabled)
            return false;

        if (!AppliesToEnvironment(action, deploymentEnvironmentId))
            return false;

        if (action.Channels != null && action.Channels.Count > 0
            && !action.Channels.Contains(deploymentChannelId))
            return false;

        return true;
    }

    private static bool AppliesToEnvironment(DeploymentActionDto action, int environmentId)
    {
        var hasInclusion = action.Environments != null && action.Environments.Count > 0;
        var hasExclusion = action.ExcludedEnvironments != null && action.ExcludedEnvironments.Count > 0;

        if (!hasInclusion && !hasExclusion)
            return true;

        if (hasExclusion && action.ExcludedEnvironments.Contains(environmentId))
            return false;

        if (hasInclusion && !action.Environments.Contains(environmentId))
            return false;

        return true;
    }

    private static bool EvaluateCondition(string condition, bool previousStepSucceeded)
    {
        return condition switch
        {
            "Always" => true,
            "Failure" => !previousStepSucceeded,
            "Variable" => true,
            null or "" => previousStepSucceeded,
            _ => previousStepSucceeded // "Success" and any unknown value
        };
    }

    private static bool MatchesTargetRoles(DeploymentStepDto step, HashSet<string> targetRoles)
    {
        if (targetRoles == null)
            return true;

        var stepRolesProperty = step.Properties?
            .FirstOrDefault(p => p.PropertyName == "Octopus.Action.TargetRoles");

        if (stepRolesProperty == null || string.IsNullOrEmpty(stepRolesProperty.PropertyValue))
            return true;

        var stepRoles = DeploymentTargetFinder.ParseRoles(stepRolesProperty.PropertyValue);
        return stepRoles.Overlaps(targetRoles);
    }

    private async Task ExecuteCalamariActionAsync(ActionExecutionResult actionResult, CancellationToken ct)
    {
        var deployByCalamariScript = Common.UtilService.GetEmbeddedScriptContent("DeployByCalamari.ps1");

        // Build YAML streams from action result files
        var yamlStreams = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in actionResult.Files)
        {
            yamlStreams[file.Key] = new MemoryStream(file.Value);
        }

        var yamlNuGetPackageBytes = CreateYamlNuGetPackage(yamlStreams);
        CheckNugetPackage(yamlNuGetPackageBytes);

        var (variableJsonStream, sensitiveVariableJsonStream, sensitiveVariablesPassword) =
            CreateVariableFileStreamsAndPassword(_ctx.Variables);

        var scriptExecution = await StartDeployByCalamariScriptAsync(
            deployByCalamariScript,
            yamlNuGetPackageBytes,
            variableJsonStream,
            sensitiveVariableJsonStream,
            sensitiveVariablesPassword,
            _ctx.Target,
            _ctx.Release.Version,
            ct).ConfigureAwait(false);

        var (executionSuccess, logLines) = await ObserveDeploymentScriptAsync(scriptExecution, ct).ConfigureAwait(false);

        CaptureOutputVariables(actionResult, logLines);

        if (!executionSuccess)
        {
            throw new InvalidOperationException("Calamari deployment script failed");
        }
    }

    private async Task ExecuteDirectScriptAsync(ActionExecutionResult actionResult, CancellationToken ct)
    {
        var endpoint = ParseMachineEndpoint(_ctx.Target);

        if (endpoint == null)
            throw new InvalidOperationException($"Endpoint could not be parsed for machine {_ctx.Target.Name}");

        var scriptFiles = new List<ScriptFile>();

        foreach (var file in actionResult.Files)
        {
            scriptFiles.Add(new ScriptFile(file.Key, DataStream.FromBytes(file.Value), null));
        }

        var command = new StartScriptCommand(
            actionResult.ScriptBody,
            ScriptIsolationLevel.FullIsolation,
            TimeSpan.FromMinutes(30),
            null,
            Array.Empty<string>(),
            null,
            scriptFiles.ToArray());

        var scriptClient = _halibutRuntime.CreateAsyncClient<IScriptService, IAsyncScriptService>(endpoint);
        var ticket = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

        Log.Information("Starting direct script on machine {MachineName} with ticket {Ticket}", _ctx.Target.Name, ticket);

        var execution = (_ctx.Target, scriptClient, ticket);
        var (executionSuccess, logLines) = await ObserveDeploymentScriptAsync(execution, ct).ConfigureAwait(false);

        CaptureOutputVariables(actionResult, logLines);

        if (!executionSuccess)
        {
            throw new InvalidOperationException("Direct script execution failed");
        }
    }

    private static void CaptureOutputVariables(ActionExecutionResult actionResult, List<string> logLines)
    {
        var outputVars = ServiceMessageParser.ParseOutputVariables(logLines);
        foreach (var kv in outputVars)
        {
            actionResult.OutputVariables[kv.Key] = kv.Value.Value;
        }
    }

    private async Task RecordCompletionAsync(bool success, string message)
    {
        var deployment = await _deploymentDataProvider.GetDeploymentByIdAsync(_ctx.Deployment.Id).ConfigureAwait(false);

        var completion = new Persistence.Entities.Deployments.DeploymentCompletion
        {
            DeploymentId = _ctx.Deployment.Id,
            CompletedTime = DateTimeOffset.UtcNow,
            State = success ? "Success" : "Failed",
            SpaceId = deployment?.SpaceId ?? 1,
            SequenceNumber = 0
        };

        await _deploymentCompletionDataProvider.AddDeploymentCompletionAsync(completion).ConfigureAwait(false);

        Log.Information(
            "Recorded deployment completion for deployment {DeploymentId}: {Status}",
            _ctx.Deployment.Id, success ? "Success" : "Failed");
    }

    private async Task<(bool Success, List<string> LogLines)> ObserveDeploymentScriptAsync(
        (Persistence.Entities.Deployments.Machine Machine, IAsyncScriptService ScriptClient, ScriptTicket Ticket) execution,
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

                    return (false, new List<string>());
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

        await PersistTaskLogsAsync(_ctx.Task.Id, orderedLogs, execution.Machine.Name, cancellationToken)
            .ConfigureAwait(false);

        var logLines = new List<string>();

        foreach (var log in orderedLogs)
        {
            Log.Information(
                "[Deployment Script] Machine={MachineName}, Time={Time}, Source={Source}, Message={Message}",
                execution.Machine.Name,
                log.Occurred,
                log.Source,
                log.Text);

            if (!string.IsNullOrEmpty(log.Text))
                logLines.Add(log.Text);
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

        return (success, logLines);
    }

    private static async Task CancelScriptAsync(
        (Persistence.Entities.Deployments.Machine Machine, IAsyncScriptService ScriptClient, ScriptTicket Ticket) execution,
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

        var variableJson = JsonSerializer.Serialize(nonSensitiveVariables);
        var variableStream = new MemoryStream(Encoding.UTF8.GetBytes(variableJson));

        var password = string.Empty;
        Stream sensitiveStream;

        if (sensitiveVariables.Count > 0)
        {
            password = Guid.NewGuid().ToString("N");

            var sensitiveJson = JsonSerializer.Serialize(sensitiveVariables);
            var encryption = new CalamariCompatibleEncryption(password);
            var encryptedBytes = encryption.Encrypt(sensitiveJson);

            sensitiveStream = new MemoryStream(encryptedBytes);
        }
        else
        {
            var emptyJson = "{}";
            sensitiveStream = new MemoryStream(Encoding.UTF8.GetBytes(emptyJson));
        }

        return (variableStream, sensitiveStream, password);
    }

    private async Task<Persistence.Entities.Deployments.ActivityLog> CreateActivityNodeAsync(
        int serverTaskId, long? parentId, string name, string nodeType,
        string status, int sortOrder, CancellationToken ct)
    {
        try
        {
            return await _activityLogDataProvider.AddNodeAsync(
                new Persistence.Entities.Deployments.ActivityLog
                {
                    ServerTaskId = serverTaskId,
                    ParentId = parentId,
                    Name = name,
                    NodeType = nodeType,
                    Status = status,
                    StartedAt = DateTimeOffset.UtcNow,
                    SortOrder = sortOrder
                }, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create activity log node: {Name}", name);
            return null;
        }
    }

    private async Task PersistTaskLogAsync(int serverTaskId, string category, string message,
        string source, CancellationToken ct)
    {
        try
        {
            var seq = Interlocked.Increment(ref _logSequence);
            await _serverTaskLogDataProvider.AddLogAsync(new Persistence.Entities.Deployments.ServerTaskLog
            {
                ServerTaskId = serverTaskId,
                Category = category,
                MessageText = message,
                Source = source,
                OccurredAt = DateTimeOffset.UtcNow,
                SequenceNumber = seq
            }, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist task log entry");
        }
    }

    private async Task PersistTaskLogsAsync(int serverTaskId, List<ProcessOutput> logs,
        string source, CancellationToken ct)
    {
        try
        {
            var entries = logs.Select(log => new Persistence.Entities.Deployments.ServerTaskLog
            {
                ServerTaskId = serverTaskId,
                Category = log.Source == ProcessOutputSource.StdErr ? "Error" : "Info",
                MessageText = log.Text,
                Source = source,
                OccurredAt = log.Occurred,
                SequenceNumber = Interlocked.Increment(ref _logSequence)
            }).ToList();

            await _serverTaskLogDataProvider.AddLogsAsync(entries, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist task log batch");
        }
    }
}
