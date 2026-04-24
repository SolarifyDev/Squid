using Halibut;
using Halibut.Diagnostics;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Settings.Halibut;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public class HalibutMachineExecutionStrategy : IExecutionStrategy
{
    private readonly IHalibutClientFactory _halibutClientFactory;
    private readonly ICalamariPayloadBuilder _payloadBuilder;
    private readonly IHalibutScriptObserver _observer;
    private readonly IMachineCircuitBreakerRegistry _breakerRegistry;
    private readonly IMachineRuntimeCapabilitiesCache _capabilitiesCache;
    private readonly TimeSpan _defaultScriptTimeout;

    public HalibutMachineExecutionStrategy(
        IHalibutClientFactory halibutClientFactory,
        ICalamariPayloadBuilder payloadBuilder,
        IHalibutScriptObserver observer,
        HalibutSetting halibutSetting,
        IMachineCircuitBreakerRegistry breakerRegistry = null,
        IMachineRuntimeCapabilitiesCache capabilitiesCache = null)
    {
        _halibutClientFactory = halibutClientFactory;
        _payloadBuilder = payloadBuilder;
        _observer = observer;
        _breakerRegistry = breakerRegistry;
        _capabilitiesCache = capabilitiesCache;
        _defaultScriptTimeout = TimeSpan.FromMinutes(Math.Max(1, halibutSetting.Polling.ScriptTimeoutMinutes));
    }

    public async Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
    {
        var plan = ScriptExecutionPlanFactory.Create(request);
        var endpoint = ParseMachineEndpoint(request.Machine);

        if (endpoint == null)
            throw new Exceptions.DeploymentEndpointException(request.Machine.Name);

        // Fail-fast when the machine's breaker is open: the recent history says the
        // agent is unreachable, so don't burn a full script timeout re-discovering
        // that. The breaker is recorded/updated inside ExecuteWithBreakerAsync so
        // the success/failure of *this* call feeds back into the state machine.
        var breaker = _breakerRegistry?.GetOrCreate(request.Machine.Id);
        breaker?.ThrowIfOpen();

        // P0-E.3: log the protocol dispatch decision from the capabilities cache.
        // Observability today; the branch site is already wired for E.2's V2 rollout.
        LogProtocolDispatchDecision(request.Machine.Id, request.Machine.Name);

        var scriptClient = _halibutClientFactory.CreateClient(endpoint);

        try
        {
            ScriptExecutionResult result;
            if (plan is PackagedPayloadExecutionPlan packagedPlan)
                result = await ExecuteCalamariViaHalibutAsync(packagedPlan, scriptClient, endpoint, ct).ConfigureAwait(false);
            else
                result = await ExecuteDirectScriptViaHalibutAsync((DirectScriptExecutionPlan)plan, scriptClient, endpoint, ct).ConfigureAwait(false);

            breaker?.RecordSuccess();
            return result;
        }
        catch (HalibutClientException)
        {
            breaker?.RecordFailure();
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            breaker?.RecordFailure();
            throw;
        }
    }

    private async Task<ScriptExecutionResult> ExecuteCalamariViaHalibutAsync(
        PackagedPayloadExecutionPlan plan, IAsyncScriptService scriptClient, ServiceEndPoint endpoint, CancellationToken ct)
    {
        var request = plan.Request;
        var payload = _payloadBuilder.Build(request, request.Syntax);

        var scriptBody = payload.FillTemplate(
            $"./{payload.PackageFileName}",
            "./variables.json",
            "./sensitiveVariables.json");

        var scriptFiles = new[]
        {
            new ScriptFile(payload.PackageFileName, DataStream.FromBytes(payload.PackageBytes), null),
            new ScriptFile("variables.json", DataStream.FromBytes(payload.VariableBytes), null),
            new ScriptFile("sensitiveVariables.json", DataStream.FromBytes(payload.SensitiveBytes), payload.SensitivePassword)
        };

        var scriptTimeout = request.Timeout ?? _defaultScriptTimeout;
        var ticketId = GenerateTicketId(request.ServerTaskId, request.StepName, request.ActionName, request.Machine.Id);
        var scriptTicket = new ScriptTicket(ticketId);

        var command = new StartScriptCommand(
            scriptTicket,
            scriptBody,
            ScriptIsolationLevel.FullIsolation,
            scriptTimeout,
            null,
            Array.Empty<string>(),
            ticketId,
            TimeSpan.Zero,
            scriptFiles)
        {
            ScriptSyntax = MapSyntax(request.Syntax),
            TargetNamespace = request.TargetNamespace
        };

        var startResponse = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

        Log.Information("[Deploy] Starting packaged YAML deployment on agent {MachineName} with ticket {Ticket}",
            request.Machine.Name, scriptTicket);

        return await _observer.ObserveAndCompleteAsync(request.Machine, scriptClient, scriptTicket, scriptTimeout, ct, request.Masker, startResponse, endpoint).ConfigureAwait(false);
    }

    private async Task<ScriptExecutionResult> ExecuteDirectScriptViaHalibutAsync(
        DirectScriptExecutionPlan plan, IAsyncScriptService scriptClient, ServiceEndPoint endpoint, CancellationToken ct)
    {
        var request = plan.Request;
        var (variableBytes, sensitiveBytes, password) =
            ScriptExecutionHelper.CreateVariableFileContents(request.Variables);

        var scriptFiles = BuildDirectScriptFiles(request.DeploymentFiles, variableBytes, sensitiveBytes, password);
        var scriptTimeout = request.Timeout ?? _defaultScriptTimeout;
        var ticketId = GenerateTicketId(request.ServerTaskId, request.StepName, request.ActionName, request.Machine.Id);
        var scriptTicket = new ScriptTicket(ticketId);

        var command = new StartScriptCommand(
            scriptTicket,
            request.ScriptBody,
            ScriptIsolationLevel.FullIsolation,
            scriptTimeout,
            null,
            Array.Empty<string>(),
            ticketId,
            TimeSpan.Zero,
            scriptFiles)
        {
            ScriptSyntax = MapSyntax(request.Syntax),
            TargetNamespace = request.TargetNamespace
        };

        var startResponse = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

        Log.Information("[Deploy] Starting direct script on agent {MachineName} with ticket {Ticket}",
            request.Machine.Name, scriptTicket);

        return await _observer.ObserveAndCompleteAsync(request.Machine, scriptClient, scriptTicket, scriptTimeout, ct, request.Masker, startResponse, endpoint).ConfigureAwait(false);
    }

    private static ScriptFile[] BuildDirectScriptFiles(
        DeploymentFileCollection deploymentFiles,
        byte[] variableBytes,
        byte[] sensitiveBytes,
        string password)
    {
        var files = deploymentFiles
            .Select(file => new ScriptFile(file.RelativePath, DataStream.FromBytes(file.Content), null))
            .ToList();

        files.Add(new ScriptFile("variables.json", DataStream.FromBytes(variableBytes), null));

        if (!string.IsNullOrEmpty(password))
            files.Add(new ScriptFile("sensitiveVariables.json", DataStream.FromBytes(sensitiveBytes), password));

        return files.ToArray();
    }

    /// <summary>
    /// Translates the server-side script syntax to the Tentacle's wire enum.
    /// All five syntaxes the Tentacle's <c>LocalScriptService</c> can execute
    /// natively are passed through; an unknown syntax is rejected loudly rather
    /// than silently downgraded to <c>Bash</c>, which previously caused Python
    /// scripts to be handed to <c>bash script.sh</c> and crash with
    /// <c>"import: command not found"</c>.
    /// </summary>
    internal static ScriptType MapSyntax(Message.Models.Deployments.Execution.ScriptSyntax syntax)
    {
        return syntax switch
        {
            Message.Models.Deployments.Execution.ScriptSyntax.PowerShell => ScriptType.PowerShell,
            Message.Models.Deployments.Execution.ScriptSyntax.Bash => ScriptType.Bash,
            Message.Models.Deployments.Execution.ScriptSyntax.Python => ScriptType.Python,
            Message.Models.Deployments.Execution.ScriptSyntax.CSharp => ScriptType.CSharp,
            Message.Models.Deployments.Execution.ScriptSyntax.FSharp => ScriptType.FSharp,
            _ => throw new InvalidOperationException($"Unsupported script syntax for Tentacle execution: {syntax}")
        };
    }

    internal static string GenerateTicketId(int serverTaskId, string stepName, string actionName, int machineId)
    {
        var input = $"{serverTaskId}|{stepName}|{actionName}|{machineId}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    /// <summary>
    /// P0-E.3: emit a structured log line naming which script-service protocol
    /// version we're about to dispatch, based on the cached capabilities from the
    /// last health check. Server-side V2 isn't implemented yet (E.2, 🏔️ ARCH), so
    /// we always dispatch V1 regardless of the cache — but the read site is now
    /// wired and operators get visibility today.
    /// </summary>
    private void LogProtocolDispatchDecision(int machineId, string machineName)
    {
        if (_capabilitiesCache == null) return;

        var caps = _capabilitiesCache.TryGet(machineId);

        if (caps == MachineRuntimeCapabilities.Empty)
        {
            Log.Debug(
                "[Deploy] Machine {MachineName} has no cached capabilities — dispatching V1 " +
                "(cache cold; first health check hasn't landed yet)",
                machineName);
            return;
        }

        var version = caps.SupportsScriptServiceV2 ? "v2-capable" : "v1-only";
        Log.Debug(
            "[Deploy] Machine {MachineName} protocol: {ProtocolVersion} (cached services: " +
            "[{Services}]). Dispatching V1 — V2 server-side not yet implemented (E.2).",
            machineName, version, string.Join(", ", caps.SupportedServices));
    }

    internal static ServiceEndPoint? ParseMachineEndpoint(Persistence.Entities.Deployments.Machine machine)
    {
        try
        {
            var style = Machines.EndpointJsonHelper.GetField(machine.Endpoint, "CommunicationStyle");

            var endpoint = style == nameof(Message.Enums.CommunicationStyle.TentacleListening)
                ? Machines.EndpointJsonHelper.ParseTentacleListeningEndpoint(machine.Endpoint)
                : Machines.EndpointJsonHelper.ParseHalibutEndpoint(machine.Endpoint);

            if (endpoint == null)
                Log.Warning("[Deploy] Machine {MachineName} has missing endpoint connection fields in endpoint JSON", machine.Name);

            return endpoint;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Failed to parse endpoint for machine {MachineName}", machine.Name);
            return null;
        }
    }
}
