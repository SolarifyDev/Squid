using System.Text;
using System.Text.Json;
using Halibut;
using Halibut.Diagnostics;
using Squid.Core.Commands.Tentacle;
using Squid.Core.Extensions;
using Squid.Core.Services.Common;
using Squid.Core.Services.Tentacle;
using Squid.Core.Settings.GithubPackage;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments.Kubernetes;

public class KubernetesAgentExecutionStrategy : IExecutionStrategy
{
    private readonly IHalibutClientFactory _halibutClientFactory;
    private readonly IYamlNuGetPacker _yamlNuGetPacker;
    private readonly CalamariGithubPackageSetting _calamariGithubPackageSetting;

    public KubernetesAgentExecutionStrategy(
        IHalibutClientFactory halibutClientFactory,
        IYamlNuGetPacker yamlNuGetPacker,
        CalamariGithubPackageSetting calamariGithubPackageSetting)
    {
        _halibutClientFactory = halibutClientFactory;
        _yamlNuGetPacker = yamlNuGetPacker;
        _calamariGithubPackageSetting = calamariGithubPackageSetting;
    }

    public bool CanHandle(string communicationStyle)
        => string.Equals(communicationStyle, "KubernetesAgent", StringComparison.OrdinalIgnoreCase);

    public async Task<ScriptExecutionResult> ExecuteScriptAsync(
        ScriptExecutionRequest request, CancellationToken ct)
    {
        var endpoint = ParseMachineEndpoint(request.Machine);

        if (endpoint == null)
            throw new Exceptions.DeploymentEndpointException(request.Machine.Name);

        var scriptClient = _halibutClientFactory.CreateClient(endpoint);

        if (request.CalamariCommand != null)
            return await ExecuteCalamariViaHalibutAsync(request, scriptClient, ct).ConfigureAwait(false);

        return await ExecuteDirectScriptViaHalibutAsync(request, scriptClient, ct).ConfigureAwait(false);
    }

    private async Task<ScriptExecutionResult> ExecuteCalamariViaHalibutAsync(
        ScriptExecutionRequest request, IAsyncScriptService scriptClient, CancellationToken ct)
    {
        var deployByCalamariScript = UtilService.GetEmbeddedScriptContent("DeployByCalamari.ps1");

        var yamlNuGetPackageBytes = CreateYamlNuGetPackage(request.Files);

        var (variableJsonStream, sensitiveVariableJsonStream, sensitiveVariablesPassword) =
            ScriptExecutionHelper.CreateVariableFileStreams(request.Variables);

        var packageBytes = yamlNuGetPackageBytes ?? Array.Empty<byte>();
        var variableBytes = ReadAllBytes(variableJsonStream);
        var sensitiveBytes = ReadAllBytes(sensitiveVariableJsonStream);

        var packageFileName = $"squid.{request.ReleaseVersion}.nupkg";
        const string variableFileName = "variables.json";
        const string sensitiveVariableFileName = "sensitiveVariables.json";

        var packageFilePath = $".\\{packageFileName}";
        var variableFilePath = $".\\{variableFileName}";
        var sensitiveVariableFilePath = string.IsNullOrEmpty(sensitiveVariablesPassword)
            ? string.Empty
            : $".\\{sensitiveVariableFileName}";

        var calamariVersion = GetCalamariVersion();

        var scriptBody = deployByCalamariScript
            .Replace("{{PackageFilePath}}", packageFilePath, StringComparison.Ordinal)
            .Replace("{{VariableFilePath}}", variableFilePath, StringComparison.Ordinal)
            .Replace("{{SensitiveVariableFile}}", sensitiveVariableFilePath, StringComparison.Ordinal)
            .Replace("{{SensitiveVariablePassword}}", sensitiveVariablesPassword, StringComparison.Ordinal)
            .Replace("{{CalamariVersion}}", calamariVersion + $"/{request.Machine.OperatingSystem.GetDescription()}", StringComparison.Ordinal);

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

        var ticket = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

        Log.Information("Starting DeployByCalamari on agent {MachineName} with ticket {Ticket}",
            request.Machine.Name, ticket);

        return await ObserveAndCompleteAsync(request.Machine, scriptClient, ticket, ct).ConfigureAwait(false);
    }

    private async Task<ScriptExecutionResult> ExecuteDirectScriptViaHalibutAsync(
        ScriptExecutionRequest request, IAsyncScriptService scriptClient, CancellationToken ct)
    {
        var scriptFiles = request.Files
            .Select(file => new ScriptFile(file.Key, DataStream.FromBytes(file.Value), null))
            .ToArray();

        var command = new StartScriptCommand(
            request.ScriptBody,
            ScriptIsolationLevel.FullIsolation,
            TimeSpan.FromMinutes(30),
            null,
            Array.Empty<string>(),
            null,
            scriptFiles);

        var ticket = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

        Log.Information("Starting direct script on agent {MachineName} with ticket {Ticket}",
            request.Machine.Name, ticket);

        return await ObserveAndCompleteAsync(request.Machine, scriptClient, ticket, ct).ConfigureAwait(false);
    }

    private static async Task<ScriptExecutionResult> ObserveAndCompleteAsync(
        Persistence.Entities.Deployments.Machine machine,
        IAsyncScriptService scriptClient,
        ScriptTicket ticket,
        CancellationToken ct)
    {
        var scriptTimeout = TimeSpan.FromMinutes(3);
        var startTime = DateTime.UtcNow;
        var statusResponse = new ScriptStatusResponse(ticket, ProcessState.Pending, 0, new List<ProcessOutput>(), 0);
        var allLogs = new List<ProcessOutput>();

        while (statusResponse.State != ProcessState.Complete)
        {
            if (DateTime.UtcNow - startTime > scriptTimeout)
            {
                Log.Warning("Script execution timeout on agent {MachineName}, cancelling", machine.Name);
                await TryCancelScriptAsync(scriptClient, ticket, statusResponse.NextLogSequence).ConfigureAwait(false);

                return new ScriptExecutionResult { Success = false, ExitCode = -1 };
            }

            ct.ThrowIfCancellationRequested();

            statusResponse = await scriptClient.GetStatusAsync(
                new ScriptStatusRequest(ticket, statusResponse.NextLogSequence)).ConfigureAwait(false);

            allLogs.AddRange(statusResponse.Logs);
            LogOutput(statusResponse.Logs, machine.Name);

            if (statusResponse.State != ProcessState.Complete)
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        var completeResponse = await scriptClient.CompleteScriptAsync(
            new CompleteScriptCommand(ticket, statusResponse.NextLogSequence)).ConfigureAwait(false);

        allLogs.AddRange(completeResponse.Logs);

        var logLines = allLogs
            .OrderBy(l => l.Occurred)
            .Where(l => !string.IsNullOrEmpty(l.Text))
            .Select(l => l.Text)
            .ToList();

        var success = completeResponse.ExitCode == 0;

        if (!success)
            Log.Error("Script failed on agent {MachineName} with exit code {ExitCode}",
                machine.Name, completeResponse.ExitCode);
        else
            Log.Information("Script completed successfully on agent {MachineName}", machine.Name);

        return new ScriptExecutionResult
        {
            Success = success,
            LogLines = logLines,
            ExitCode = completeResponse.ExitCode
        };
    }

    private static void LogOutput(List<ProcessOutput> logs, string machineName)
    {
        foreach (var log in logs)
            Log.Information("[Agent Script] Machine={MachineName}, Source={Source}, Message={Message}",
                machineName, log.Source, log.Text);
    }

    private static async Task TryCancelScriptAsync(
        IAsyncScriptService scriptClient, ScriptTicket ticket, long lastLogSequence)
    {
        try
        {
            await scriptClient.CancelScriptAsync(new CancelScriptCommand(ticket, lastLogSequence)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to cancel script with ticket {Ticket}", ticket.TaskId);
        }
    }

    private static ServiceEndPoint? ParseMachineEndpoint(Persistence.Entities.Deployments.Machine machine)
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
            Log.Warning(ex, "Failed to parse endpoint for machine {MachineName}", machine.Name);
            return null;
        }
    }

    private byte[] CreateYamlNuGetPackage(Dictionary<string, byte[]> files)
    {
        if (files == null || files.Count == 0)
            return Array.Empty<byte>();

        var yamlStreams = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
            yamlStreams[file.Key] = new MemoryStream(file.Value);

        return _yamlNuGetPacker.CreateNuGetPackageFromYamlStreams(yamlStreams);
    }

    private string GetCalamariVersion()
        => string.IsNullOrWhiteSpace(_calamariGithubPackageSetting.Version)
            ? "28.2.1"
            : _calamariGithubPackageSetting.Version;

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream == null) return Array.Empty<byte>();

        if (stream.CanSeek) stream.Position = 0;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
