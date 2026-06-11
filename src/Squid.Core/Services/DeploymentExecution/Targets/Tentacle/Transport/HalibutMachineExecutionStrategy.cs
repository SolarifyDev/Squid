using Halibut;
using Halibut.Diagnostics;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Settings.Halibut;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

/// <summary>
/// Halibut-based <see cref="IExecutionStrategy"/> for both Tentacle Listening
/// (server initiates outbound TCP+mTLS) and polling-style transports
/// (KubernetesAgent pod, Linux/Windows polling tentacle calling home over
/// <c>poll://</c>).
///
/// <para><b> namespace move</b>: previously lived under
/// <c>Squid.Core.Services.DeploymentExecution.Infrastructure</c>, which made
/// it look like a generic infrastructure helper. It's actually the canonical
/// Tentacle-protocol execution strategy — Halibut IS the Tentacle wire
/// protocol. Moved into <c>Targets/Tentacle/Transport/</c> alongside the other
/// Tentacle composition pieces so a new transport contributor doesn't try to
/// extend it from the wrong place. Same structural-genericity fix as
///  (<c>EndpointVariableFactory</c>).</para>
/// </summary>
public class HalibutMachineExecutionStrategy : IExecutionStrategy
{
    private readonly IHalibutClientFactory _halibutClientFactory;
    private readonly ICalamariPayloadBuilder _payloadBuilder;
    private readonly IHalibutScriptObserver _observer;
    private readonly IMachineCircuitBreakerRegistry _breakerRegistry;
    private readonly IMachineRuntimeCapabilitiesCache _capabilitiesCache;
    private readonly IInFlightScriptStore _inFlightStore;
    private readonly TimeSpan _defaultScriptTimeout;

    public HalibutMachineExecutionStrategy(
        IHalibutClientFactory halibutClientFactory,
        ICalamariPayloadBuilder payloadBuilder,
        IHalibutScriptObserver observer,
        HalibutSetting halibutSetting,
        IMachineCircuitBreakerRegistry breakerRegistry = null,
        IMachineRuntimeCapabilitiesCache capabilitiesCache = null,
        IInFlightScriptStore inFlightStore = null)
    {
        _halibutClientFactory = halibutClientFactory;
        _payloadBuilder = payloadBuilder;
        _observer = observer;
        _breakerRegistry = breakerRegistry;
        _capabilitiesCache = capabilitiesCache;
        _inFlightStore = inFlightStore;
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

        //  (audit B.2): bounded polling-work admission gate.
        // Pre-fix, an offline polling Tentacle could absorb 1000+ queued
        // dispatches — Halibut's pending-request queue grew unbounded → OOM.
        // We pre-check before CreateClient, reject fast above the cap so the
        // Hangfire worker is freed immediately and the queue stays bounded.
        var maxPending = HalibutPollingWorkAdmission.ResolveMaxPendingWorkPerAgent();

        if (!HalibutPollingWorkAdmission.TryAdmit(request.Machine.Id, maxPending, out var currentInFlight))
        {
            Log.Warning(
                "[HALIBUT] Polling work admission REJECTED for machine {MachineId} ({MachineName}): " +
                "{Current} in-flight ≥ cap {Max}. Either the agent is offline or the cap is too low " +
                "for legit burst (override via {EnvVar}).",
                request.Machine.Id, request.Machine.Name, currentInFlight, maxPending,
                HalibutPollingWorkAdmission.MaxPendingWorkPerAgentEnvVar);
            throw new Exceptions.PollingWorkAdmissionExceededException(request.Machine.Id, currentInFlight, maxPending);
        }

        try
        {
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
        finally
        {
            // : release the admission slot on every exit path
            // (success, exception, cancellation). Without finally, an
            // exception-throwing dispatch would leak admission counts and
            // the per-machine cap would slowly drift to 0 effective slots.
            HalibutPollingWorkAdmission.Release(request.Machine.Id);
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
        // Ticket is fresh-per-attempt (Guid) so a retry doesn't trap on agent-side
        // state from the previous attempt. IsolationMutexName is MACHINE-SCOPED
        // (the 5th ctor arg) so every FullIsolation script on this machine —
        // deployments AND upgrades, all via ScriptIsolationMutexNames.ForMachine —
        // serialises behind one writer lock. The server task id rides the 7th arg
        // (taskId) purely for correlation.
        var ticketId = GenerateTicketId(request.ServerTaskId, request.StepName, request.ActionName, request.Machine.Id);
        var scriptTicket = new ScriptTicket(ticketId);

        var command = new StartScriptCommand(
            scriptTicket,
            scriptBody,
            ScriptIsolationLevel.FullIsolation,
            scriptTimeout,
            ScriptIsolationMutexNames.ForMachine(request.Machine.Id),
            Array.Empty<string>(),
            request.ServerTaskId.ToString(),
            TimeSpan.Zero,
            scriptFiles)
        {
            ScriptSyntax = MapSyntax(request.Syntax),
            TargetNamespace = request.TargetNamespace
        };

        return await DispatchOrReattachAsync(request, scriptClient, endpoint, scriptTicket, command, scriptTimeout, ct).ConfigureAwait(false);
    }

    private async Task<ScriptExecutionResult> ExecuteDirectScriptViaHalibutAsync(
        DirectScriptExecutionPlan plan, IAsyncScriptService scriptClient, ServiceEndPoint endpoint, CancellationToken ct)
    {
        var request = plan.Request;
        var (variableBytes, sensitiveBytes, password) =
            ScriptExecutionHelper.CreateVariableFileContents(request.Variables);

        var scriptFiles = BuildDirectScriptFiles(request.DeploymentFiles, variableBytes, sensitiveBytes, password);
        var scriptTimeout = request.Timeout ?? _defaultScriptTimeout;
        // See ExecuteCalamariViaHalibutAsync above for the ticket-vs-mutex-name
        // rationale: ticket is Guid-per-attempt; IsolationMutexName (arg 5) is the
        // machine-scoped name that serialises FullIsolation scripts on the agent.
        var ticketId = GenerateTicketId(request.ServerTaskId, request.StepName, request.ActionName, request.Machine.Id);
        var scriptTicket = new ScriptTicket(ticketId);

        var command = new StartScriptCommand(
            scriptTicket,
            request.ScriptBody,
            ScriptIsolationLevel.FullIsolation,
            scriptTimeout,
            ScriptIsolationMutexNames.ForMachine(request.Machine.Id),
            Array.Empty<string>(),
            request.ServerTaskId.ToString(),
            TimeSpan.Zero,
            scriptFiles)
        {
            ScriptSyntax = MapSyntax(request.Syntax),
            TargetNamespace = request.TargetNamespace
        };

        return await DispatchOrReattachAsync(request, scriptClient, endpoint, scriptTicket, command, scriptTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatch a script to the agent and observe it to completion — or, on a
    /// resumed deployment, re-attach to a still-running script from a prior
    /// (crashed) run instead of launching a duplicate. The in-flight ScriptTicket
    /// is recorded <b>before</b> <c>StartScript</c> and cleared once observation
    /// completes, so a server crash (or a lost RPC response) anywhere from the
    /// moment the agent launches the script leaves a durable pointer the next run
    /// can re-attach to — never a duplicate dispatch.
    /// </summary>
    private async Task<ScriptExecutionResult> DispatchOrReattachAsync(
        ScriptExecutionRequest request, IAsyncScriptService scriptClient, ServiceEndPoint endpoint,
        ScriptTicket scriptTicket, StartScriptCommand command, TimeSpan scriptTimeout, CancellationToken ct)
    {
        var reattached = await TryReattachAsync(request, scriptClient, endpoint, scriptTimeout, ct).ConfigureAwait(false);
        if (reattached != null) return reattached;

        // Record-before-RPC: persist the ticket we are about to dispatch BEFORE
        // firing StartScript. If the server crashes (or the response is lost) in
        // the window where the agent has launched the script but the server has
        // not yet recorded the ticket, a resume would otherwise find no pointer and
        // dispatch a fresh ticket — running the (non-idempotent) script a SECOND
        // time. With the record first, resume always knows a ticket to re-probe:
        // TryReattachAsync re-attaches if the agent has it, and falls through to a
        // clean fresh dispatch (via the Complete+UnknownResult sentinel) if the
        // script never actually started. The agent is idempotent for a known
        // ticket, so even a retried StartScript returns status, not a second run.
        if (_inFlightStore != null)
            await _inFlightStore.RecordDispatchedAsync(request.ServerTaskId, request.Machine.Id, scriptTicket.TaskId, ct).ConfigureAwait(false);

        var startResponse = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

        Log.Information("[Deploy] Dispatching script to agent {MachineName} with ticket {Ticket}",
            request.Machine.Name, scriptTicket);

        try
        {
            return await _observer.ObserveAndCompleteAsync(request.Machine, scriptClient, scriptTicket, scriptTimeout, ct, request.Masker, startResponse, endpoint, request.OutputSink).ConfigureAwait(false);
        }
        finally
        {
            if (_inFlightStore != null)
                await _inFlightStore.ClearAsync(request.ServerTaskId, request.Machine.Id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// If a prior run recorded an in-flight ticket for this machine and the agent
    /// still has a usable script for it, observe that script to completion and
    /// return its result (no duplicate dispatch). Returns <see langword="null"/>
    /// — meaning "dispatch fresh" — when there is no record, the probe fails, or
    /// the agent has no usable script (<see cref="AgentHasUsableScript"/>). The
    /// fresh-dispatch fallback is exactly today's behaviour, so re-attach can only
    /// ever AVOID a duplicate, never change a deployment that wasn't resuming.
    /// </summary>
    private async Task<ScriptExecutionResult> TryReattachAsync(
        ScriptExecutionRequest request, IAsyncScriptService scriptClient, ServiceEndPoint endpoint, TimeSpan scriptTimeout, CancellationToken ct)
    {
        if (_inFlightStore == null) return null;

        var existingTicketId = await _inFlightStore.TryGetTicketAsync(request.ServerTaskId, request.Machine.Id, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(existingTicketId)) return null;

        var existingTicket = new ScriptTicket(existingTicketId);

        ScriptStatusResponse probe;
        try
        {
            probe = await scriptClient.GetStatusAsync(new ScriptStatusRequest(existingTicket, 0)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Information(ex, "[Deploy] Re-attach probe failed for agent {MachineName} (ticket {Ticket}); dispatching fresh.",
                request.Machine.Name, existingTicketId);
            await _inFlightStore.ClearAsync(request.ServerTaskId, request.Machine.Id, ct).ConfigureAwait(false);
            return null;
        }

        if (!AgentHasUsableScript(probe))
        {
            await _inFlightStore.ClearAsync(request.ServerTaskId, request.Machine.Id, ct).ConfigureAwait(false);
            return null;
        }

        Log.Information("[Deploy] Re-attaching to in-flight script on agent {MachineName} (ticket {Ticket}, state {State}) after resume — skipping duplicate dispatch.",
            request.Machine.Name, existingTicketId, probe.State);

        try
        {
            return await _observer.ObserveAndCompleteAsync(request.Machine, scriptClient, existingTicket, scriptTimeout, ct, request.Masker, probe, endpoint, request.OutputSink).ConfigureAwait(false);
        }
        finally
        {
            await _inFlightStore.ClearAsync(request.ServerTaskId, request.Machine.Id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a <c>GetStatus</c> probe shows the agent still holds the script,
    /// so we re-attach instead of dispatching a duplicate. The Tentacle returns
    /// <c>Complete + UnknownResult (-1)</c> for an UNKNOWN ticket — the single
    /// "agent doesn't have it" signal — so ONLY that case dispatches fresh.
    ///
    /// <para>Every other state means the agent demonstrably holds the ticket and
    /// MUST be re-attached to — including <see cref="ProcessState.Pending"/>: a
    /// queued-but-not-started script left by the crashed run would otherwise run
    /// <i>alongside</i> a fresh dispatch (new ticket), causing the exact
    /// double-execution this feature prevents. A real script that genuinely
    /// exited -1 collides with the sentinel and is conservatively re-dispatched
    /// (today's behaviour) — an accepted rare edge.</para>
    /// </summary>
    internal static bool AgentHasUsableScript(ScriptStatusResponse probe)
        => probe is not null
           && !(probe.State == ProcessState.Complete && probe.ExitCode == ScriptExitCodes.UnknownResult);

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

    /// <summary>
    /// Produces a fresh <see cref="ScriptTicket"/> id per dispatch attempt
    /// (<c>Guid.NewGuid()</c>). The (<paramref name="serverTaskId"/>,
    /// <paramref name="stepName"/>, <paramref name="actionName"/>,
    /// <paramref name="machineId"/>) tuple is IGNORED — the ticket is deliberately
    /// non-deterministic so a retry doesn't trap on agent-side state (workspace,
    /// mutex) left by the previous attempt. The arguments are kept on the signature
    /// for call-site readability and test pinning. Cross-dispatch serialisation is
    /// handled separately by the machine-scoped
    /// <see cref="ScriptIsolationMutexNames.ForMachine"/> on
    /// <see cref="StartScriptCommand.IsolationMutexName"/>, NOT by the ticket.
    ///
    /// <para>Pinned by <c>HalibutMachineExecutionStrategyRoutingTests.GenerateTicketId_*</c>.</para>
    /// </summary>
    internal static string GenerateTicketId(int serverTaskId, string stepName, string actionName, int machineId)
        => Guid.NewGuid().ToString("N");

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
