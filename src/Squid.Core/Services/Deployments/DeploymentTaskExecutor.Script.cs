using Halibut;
using System.Text;
using System.Text.Json;
using Squid.Core.Commands.Tentacle;
using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.Exceptions;
using Squid.Core.Services.Tentacle;
using Squid.Core.VariableSubstitution;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments;

public partial class DeploymentTaskExecutor
{
    private async Task ExecuteCalamariActionAsync(ActionExecutionResult actionResult, CancellationToken ct)
    {
        var deployByCalamariScript = Common.UtilService.GetEmbeddedScriptContent("DeployByCalamari.ps1");

        var yamlStreams = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in actionResult.Files)
            yamlStreams[file.Key] = new MemoryStream(file.Value);

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
            throw new DeploymentScriptException("Calamari deployment script failed", _ctx.Deployment.Id);
    }

    private async Task ExecuteDirectScriptAsync(ActionExecutionResult actionResult, CancellationToken ct)
    {
        var endpoint = ParseMachineEndpoint(_ctx.Target);

        if (endpoint == null)
            throw new DeploymentEndpointException(_ctx.Target.Name);

        var scriptFiles = actionResult.Files
            .Select(file => new ScriptFile(file.Key, DataStream.FromBytes(file.Value), null))
            .ToArray();

        var command = new StartScriptCommand(
            actionResult.ScriptBody,
            ScriptIsolationLevel.FullIsolation,
            TimeSpan.FromMinutes(30),
            null,
            Array.Empty<string>(),
            null,
            scriptFiles);

        var scriptClient = _halibutRuntime.CreateAsyncClient<IScriptService, IAsyncScriptService>(endpoint);
        var ticket = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

        Log.Information("Starting direct script on machine {MachineName} with ticket {Ticket}", _ctx.Target.Name, ticket);

        var execution = (_ctx.Target, scriptClient, ticket);
        var (executionSuccess, logLines) = await ObserveDeploymentScriptAsync(execution, ct).ConfigureAwait(false);

        CaptureOutputVariables(actionResult, logLines);

        if (!executionSuccess)
            throw new DeploymentScriptException("Direct script execution failed", _ctx.Deployment.Id);
    }

    private static void CaptureOutputVariables(ActionExecutionResult actionResult, List<string> logLines)
    {
        var outputVars = ServiceMessageParser.ParseOutputVariables(logLines);

        foreach (var kv in outputVars)
            actionResult.OutputVariables[kv.Key] = kv.Value.Value;
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
            execution.Ticket, ProcessState.Pending, 0, new List<ProcessOutput>(), 0);
        var logs = new List<ProcessOutput>();

        try
        {
            while (scriptStatusResponse.State != ProcessState.Complete)
            {
                if (DateTime.UtcNow - startTime > scriptTimeout)
                {
                    Log.Warning(
                        "Script execution timeout on machine {MachineName}, cancelling script with ticket {Ticket}",
                        execution.Machine.Name, execution.Ticket.TaskId);

                    await CancelScriptAsync(execution, scriptStatusResponse.NextLogSequence).ConfigureAwait(false);
                    return (false, new List<string>());
                }

                cancellationToken.ThrowIfCancellationRequested();

                scriptStatusResponse = await execution.ScriptClient.GetStatusAsync(
                    new ScriptStatusRequest(execution.Ticket, scriptStatusResponse.NextLogSequence)).ConfigureAwait(false);

                logs.AddRange(scriptStatusResponse.Logs);
                LogScriptOutput(scriptStatusResponse.Logs, execution.Machine.Name);

                if (scriptStatusResponse.State != ProcessState.Complete)
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warning(
                "Script observation cancelled on machine {MachineName}, cancelling script with ticket {Ticket}",
                execution.Machine.Name, execution.Ticket.TaskId);

            await CancelScriptAsync(execution, scriptStatusResponse.NextLogSequence).ConfigureAwait(false);
            throw;
        }

        return await CompleteScriptAsync(execution, logs, scriptStatusResponse, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Success, List<string> LogLines)> CompleteScriptAsync(
        (Persistence.Entities.Deployments.Machine Machine, IAsyncScriptService ScriptClient, ScriptTicket Ticket) execution,
        List<ProcessOutput> logs,
        ScriptStatusResponse lastResponse,
        CancellationToken ct)
    {
        var completeResponse = await execution.ScriptClient.CompleteScriptAsync(
            new CompleteScriptCommand(execution.Ticket, lastResponse.NextLogSequence)).ConfigureAwait(false);

        logs.AddRange(completeResponse.Logs);

        var orderedLogs = logs.OrderBy(l => l.Occurred).ToList();

        await PersistTaskLogsAsync(_ctx.Task.Id, orderedLogs, execution.Machine.Name, ct)
            .ConfigureAwait(false);

        var logLines = ExtractLogLines(orderedLogs, execution.Machine.Name);
        var success = completeResponse.ExitCode == 0;

        if (!success)
            Log.Error("Deployment script failed on machine {MachineName} with exit code {ExitCode}",
                execution.Machine.Name, completeResponse.ExitCode);
        else
            Log.Information("Deployment script completed successfully on machine {MachineName}",
                execution.Machine.Name);

        return (success, logLines);
    }

    private static void LogScriptOutput(List<ProcessOutput> logs, string machineName)
    {
        foreach (var log in logs)
            Log.Information(
                "[Deployment Script] Machine={MachineName}, Time={Time}, Source={Source}, Message={Message}",
                machineName, log.Occurred, log.Source, log.Text);
    }

    private static List<string> ExtractLogLines(List<ProcessOutput> orderedLogs, string machineName)
    {
        var logLines = new List<string>();

        foreach (var log in orderedLogs)
        {
            Log.Information(
                "[Deployment Script] Machine={MachineName}, Time={Time}, Source={Source}, Message={Message}",
                machineName, log.Occurred, log.Source, log.Text);

            if (!string.IsNullOrEmpty(log.Text))
                logLines.Add(log.Text);
        }

        return logLines;
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
                execution.Machine.Name, execution.Ticket.TaskId, cancelResponse.State);
        }
        catch (Exception ex)
        {
            Log.Error(ex,
                "Failed to cancel script on machine {MachineName}, ticket {Ticket}",
                execution.Machine.Name, execution.Ticket.TaskId);
        }
    }

    private (Stream variableJsonStream, Stream sensitiveVariableJsonStream, string password) CreateVariableFileStreamsAndPassword(
        List<VariableDto> variables)
    {
        if (variables == null || variables.Count == 0)
        {
            var emptyBytes = Encoding.UTF8.GetBytes("{}");
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
                sensitiveVariables[variable.Name] = value;
            else
                nonSensitiveVariables[variable.Name] = value;
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
            sensitiveStream = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        }

        return (variableStream, sensitiveStream, password);
    }
}
