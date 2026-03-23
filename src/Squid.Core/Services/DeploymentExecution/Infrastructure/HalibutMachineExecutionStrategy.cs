using Halibut;
using Halibut.Diagnostics;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Settings.Halibut;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public class HalibutMachineExecutionStrategy : IExecutionStrategy
{
    private readonly IHalibutClientFactory _halibutClientFactory;
    private readonly ICalamariPayloadBuilder _payloadBuilder;
    private readonly IHalibutScriptObserver _observer;
    private readonly TimeSpan _defaultScriptTimeout;

    public HalibutMachineExecutionStrategy(IHalibutClientFactory halibutClientFactory, ICalamariPayloadBuilder payloadBuilder, IHalibutScriptObserver observer, HalibutSetting halibutSetting)
    {
        _halibutClientFactory = halibutClientFactory;
        _payloadBuilder = payloadBuilder;
        _observer = observer;
        _defaultScriptTimeout = TimeSpan.FromMinutes(Math.Max(1, halibutSetting.Polling.ScriptTimeoutMinutes));
    }

    public async Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
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
        var payload = _payloadBuilder.Build(request, request.Syntax);

        var scriptBody = payload.FillTemplate(
            $"./{payload.PackageFileName}",
            "./variables.json",
            "./sensitiveVariables.json");

        scriptBody = ApplyContextPreparationIfRequired(request, scriptBody);

        var scriptFiles = new[]
        {
            new ScriptFile(payload.PackageFileName, DataStream.FromBytes(payload.PackageBytes), null),
            new ScriptFile("variables.json", DataStream.FromBytes(payload.VariableBytes), null),
            new ScriptFile("sensitiveVariables.json", DataStream.FromBytes(payload.SensitiveBytes), payload.SensitivePassword)
        };

        var scriptTimeout = request.Timeout ?? _defaultScriptTimeout;
        var ticketId = GenerateTicketId(request.ServerTaskId, request.StepName, request.ActionName, request.Machine.Id);

        var command = new StartScriptCommand(
            scriptBody,
            ScriptIsolationLevel.FullIsolation,
            scriptTimeout,
            null,
            Array.Empty<string>(),
            ticketId,
            scriptFiles)
        {
            TargetNamespace = request.TargetNamespace
        };

        var ticket = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

        Log.Information("Starting packaged YAML deployment on agent {MachineName} with ticket {Ticket}",
            request.Machine.Name, ticket);

        return await _observer.ObserveAndCompleteAsync(request.Machine, scriptClient, ticket, scriptTimeout, ct, request.Masker).ConfigureAwait(false);
    }

    private async Task<ScriptExecutionResult> ExecuteDirectScriptViaHalibutAsync(
        DirectScriptExecutionPlan plan, IAsyncScriptService scriptClient, CancellationToken ct)
    {
        var request = plan.Request;
        var (variableBytes, sensitiveBytes, password) =
            ScriptExecutionHelper.CreateVariableFileContents(request.Variables);

        var scriptFiles = BuildDirectScriptFiles(request.Files, variableBytes, sensitiveBytes, password);
        var scriptTimeout = request.Timeout ?? _defaultScriptTimeout;
        var ticketId = GenerateTicketId(request.ServerTaskId, request.StepName, request.ActionName, request.Machine.Id);

        var command = new StartScriptCommand(
            request.ScriptBody,
            ScriptIsolationLevel.FullIsolation,
            scriptTimeout,
            null,
            Array.Empty<string>(),
            ticketId,
            scriptFiles)
        {
            TargetNamespace = request.TargetNamespace
        };

        var ticket = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

        Log.Information("Starting direct script on agent {MachineName} with ticket {Ticket}",
            request.Machine.Name, ticket);

        return await _observer.ObserveAndCompleteAsync(request.Machine, scriptClient, ticket, scriptTimeout, ct, request.Masker).ConfigureAwait(false);
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

    private static string ApplyContextPreparationIfRequired(ScriptExecutionRequest request, string scriptBody)
    {
        if (request.ContextWrapper == null)
            return scriptBody;

        var scriptContext = new ScriptContext
        {
            Endpoint = request.EndpointContext,
            Syntax = request.Syntax,
            Variables = request.Variables,
            ActionProperties = request.ActionProperties
        };

        return request.ContextWrapper.WrapScript(scriptBody, scriptContext);
    }

    internal static string GenerateTicketId(int serverTaskId, string stepName, string actionName, int machineId)
    {
        var input = $"{serverTaskId}|{stepName}|{actionName}|{machineId}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    private static ServiceEndPoint? ParseMachineEndpoint(Persistence.Entities.Deployments.Machine machine)
    {
        try
        {
            var endpoint = Machines.EndpointJsonHelper.ParseHalibutEndpoint(machine.Endpoint);

            if (endpoint == null)
                Log.Warning("Machine {MachineName} has missing SubscriptionId or Thumbprint in endpoint JSON", machine.Name);

            return endpoint;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse endpoint for machine {MachineName}", machine.Name);
            return null;
        }
    }
}
