using Halibut;
using Halibut.Diagnostics;
using Squid.Core.Extensions;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.ExecutionPlans;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesAgentExecutionStrategy : IExecutionStrategy
{
    private static readonly TimeSpan ScriptExecutionTimeout = TimeSpan.FromMinutes(30);

    private readonly IHalibutClientFactory _halibutClientFactory;
    private readonly ICalamariPayloadBuilder _payloadBuilder;
    private readonly IHalibutScriptObserver _observer;

    public KubernetesAgentExecutionStrategy(
        IHalibutClientFactory halibutClientFactory,
        ICalamariPayloadBuilder payloadBuilder,
        IHalibutScriptObserver observer)
    {
        _halibutClientFactory = halibutClientFactory;
        _payloadBuilder = payloadBuilder;
        _observer = observer;
    }

    public async Task<ScriptExecutionResult> ExecuteScriptAsync(
        ScriptExecutionRequest request, CancellationToken ct)
    {
        var plan = ScriptExecutionPlanFactory.Create(request);
        var endpoint = ParseMachineEndpoint(request.Machine);

        if (endpoint == null)
            throw new Exceptions.DeploymentEndpointException(request.Machine.Name);

        var scriptClient = _halibutClientFactory.CreateClient(endpoint);

        if (plan is PackagedPayloadExecutionPlan packagedPlan)
            return await ExecuteCalamariViaHalibutAsync(packagedPlan, scriptClient, ct).ConfigureAwait(false);

        return await ExecuteDirectScriptViaHalibutAsync((DirectScriptExecutionPlan)plan, scriptClient, ct).ConfigureAwait(false);
    }

    private async Task<ScriptExecutionResult> ExecuteCalamariViaHalibutAsync(
        PackagedPayloadExecutionPlan plan, IAsyncScriptService scriptClient, CancellationToken ct)
    {
        var request = plan.Request;
        var payload = _payloadBuilder.Build(request);

        var scriptBody = payload.FillTemplate(
            $".\\{payload.PackageFileName}",
            ".\\variables.json",
            ".\\sensitiveVariables.json");

        var scriptFiles = new[]
        {
            new ScriptFile(payload.PackageFileName, DataStream.FromBytes(payload.PackageBytes), null),
            new ScriptFile("variables.json", DataStream.FromBytes(payload.VariableBytes), null),
            new ScriptFile("sensitiveVariables.json", DataStream.FromBytes(payload.SensitiveBytes), payload.SensitivePassword)
        };

        var scriptTimeout = ScriptExecutionTimeout;

        var command = new StartScriptCommand(
            scriptBody,
            ScriptIsolationLevel.FullIsolation,
            scriptTimeout,
            null,
            Array.Empty<string>(),
            null,
            scriptFiles);

        var ticket = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

        Log.Information("Starting packaged YAML deployment on agent {MachineName} with ticket {Ticket}",
            request.Machine.Name, ticket);

        return await _observer.ObserveAndCompleteAsync(request.Machine, scriptClient, ticket, scriptTimeout, ct).ConfigureAwait(false);
    }

    private async Task<ScriptExecutionResult> ExecuteDirectScriptViaHalibutAsync(
        DirectScriptExecutionPlan plan, IAsyncScriptService scriptClient, CancellationToken ct)
    {
        var request = plan.Request;
        var (variableBytes, sensitiveBytes, password) =
            ScriptExecutionHelper.CreateVariableFileContents(request.Variables);

        var scriptFiles = BuildDirectScriptFiles(request.Files, variableBytes, sensitiveBytes, password);
        var scriptTimeout = ScriptExecutionTimeout;

        var command = new StartScriptCommand(
            request.ScriptBody,
            ScriptIsolationLevel.FullIsolation,
            scriptTimeout,
            null,
            Array.Empty<string>(),
            null,
            scriptFiles);

        var ticket = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

        Log.Information("Starting direct script on agent {MachineName} with ticket {Ticket}",
            request.Machine.Name, ticket);

        return await _observer.ObserveAndCompleteAsync(request.Machine, scriptClient, ticket, scriptTimeout, ct).ConfigureAwait(false);
    }

    private static ScriptFile[] BuildDirectScriptFiles(
        Dictionary<string, byte[]> requestFiles,
        byte[] variableBytes,
        byte[] sensitiveBytes,
        string password)
    {
        var files = requestFiles
            .Select(file => new ScriptFile(file.Key, DataStream.FromBytes(file.Value), null))
            .ToList();

        files.Add(new ScriptFile("variables.json", DataStream.FromBytes(variableBytes), null));

        if (!string.IsNullOrEmpty(password))
            files.Add(new ScriptFile("sensitiveVariables.json", DataStream.FromBytes(sensitiveBytes), password));

        return files.ToArray();
    }

    private static ServiceEndPoint? ParseMachineEndpoint(Persistence.Entities.Deployments.Machine machine)
    {
        try
        {
            var uri = machine.Uri;

            if (string.IsNullOrEmpty(uri) && !string.IsNullOrEmpty(machine.PollingSubscriptionId))
                uri = $"poll://{machine.PollingSubscriptionId}/";

            if (string.IsNullOrEmpty(uri) || string.IsNullOrEmpty(machine.Thumbprint))
            {
                Log.Warning("Machine {MachineName} has missing Uri or Thumbprint", machine.Name);
                return null;
            }

            return new ServiceEndPoint(uri, machine.Thumbprint, HalibutTimeoutsAndLimits.RecommendedValues());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse endpoint for machine {MachineName}", machine.Name);
            return null;
        }
    }
}
