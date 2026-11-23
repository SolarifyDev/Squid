using Halibut;
using Halibut.Diagnostics;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Settings.Halibut;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

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
///
/// <para><b>Seam boundary</b>: this class owns everything that is the same whichever script-service
/// protocol version the agent speaks — endpoint parsing, circuit breaker, polling-work admission, ticket
/// generation, the machine-scoped isolation mutex name, the in-flight record-before-RPC / clear /
/// re-attach protocol, RPC retry wrapping, the observer and the remote-cancel decision. Only "open a
/// client, start a script from a <see cref="ScriptDispatchSpec"/>, recognise an unknown ticket" varies,
/// and that lives behind <see cref="IScriptDispatcher"/> (V1: <c>Dispatchers/V1ScriptDispatcher</c>).
/// A new protocol version is a sibling dispatcher; this class does not change.</para>
/// </summary>
public class HalibutMachineExecutionStrategy : IExecutionStrategy
{
    private readonly ICalamariPayloadBuilder _payloadBuilder;
    private readonly IHalibutScriptObserver _observer;
    private readonly IMachineCircuitBreakerRegistry _breakerRegistry;
    private readonly IMachineRuntimeCapabilitiesCache _capabilitiesCache;
    private readonly IInFlightScriptStore _inFlightStore;
    private readonly RpcRetryHandler _rpcRetry;
    private readonly IScriptDispatcher _scriptDispatcher;
    private readonly TimeSpan _defaultScriptTimeout;

    public HalibutMachineExecutionStrategy(
        IHalibutClientFactory halibutClientFactory,
        ICalamariPayloadBuilder payloadBuilder,
        IHalibutScriptObserver observer,
        HalibutSetting halibutSetting,
        IMachineCircuitBreakerRegistry breakerRegistry = null,
        IMachineRuntimeCapabilitiesCache capabilitiesCache = null,
        IInFlightScriptStore inFlightStore = null,
        RpcRetryHandler rpcRetry = null,
        IScriptDispatcher scriptDispatcher = null)
    {
        _payloadBuilder = payloadBuilder;
        _observer = observer;
        _breakerRegistry = breakerRegistry;
        _capabilitiesCache = capabilitiesCache;
        _inFlightStore = inFlightStore;
        _rpcRetry = rpcRetry ?? new RpcRetryHandler(halibutSetting.RpcRetry);
        _scriptDispatcher = scriptDispatcher ?? new V1ScriptDispatcher(halibutClientFactory);
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

            var scriptClient = _scriptDispatcher.CreateClient(endpoint);

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

        var dispatch = BuildDispatch(request, scriptClient, endpoint, scriptBody, scriptFiles);

        return await DispatchOrReattachAsync(dispatch, ct).ConfigureAwait(false);
    }

    private async Task<ScriptExecutionResult> ExecuteDirectScriptViaHalibutAsync(
        DirectScriptExecutionPlan plan, IAsyncScriptService scriptClient, ServiceEndPoint endpoint, CancellationToken ct)
    {
        var request = plan.Request;
        var (variableBytes, sensitiveBytes, password) =
            ScriptExecutionHelper.CreateVariableFileContents(request.Variables);

        var scriptFiles = BuildDirectScriptFiles(request.DeploymentFiles, request.PackageReferences, variableBytes, sensitiveBytes, password);
        var dispatch = BuildDispatch(request, scriptClient, endpoint, request.ScriptBody, scriptFiles);

        return await DispatchOrReattachAsync(dispatch, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Everything one dispatch needs, protocol-neutral: the ticket / timeout / mutex name are decided here
    /// (shared by every <see cref="IScriptDispatcher"/>), and the dispatcher only validates the spec and
    /// prepares its start call — before any in-flight record or agent probe.
    /// </summary>
    private DispatchContext BuildDispatch(ScriptExecutionRequest request, IAsyncScriptService scriptClient, ServiceEndPoint endpoint, string scriptBody, ScriptFile[] scriptFiles)
    {
        // Ticket is fresh-per-attempt (Guid) so a retry doesn't trap on agent-side
        // state from the previous attempt. The isolation mutex name is MACHINE-SCOPED
        // so every FullIsolation script on this machine — deployments AND upgrades,
        // all via ScriptIsolationMutexNames.ForMachine — serialises behind one writer
        // lock. The server task id rides along purely for correlation.
        var spec = new ScriptDispatchSpec
        {
            Ticket = new ScriptTicket(GenerateTicketId(request.ServerTaskId, request.StepName, request.ActionName, request.Machine.Id)),
            ScriptBody = scriptBody,
            Files = scriptFiles,
            Timeout = request.Timeout ?? _defaultScriptTimeout,
            IsolationMutexName = ScriptIsolationMutexNames.ForMachine(request.Machine.Id),
            ServerTaskId = request.ServerTaskId,
            Syntax = request.Syntax,
            TargetNamespace = request.TargetNamespace
        };

        var start = _scriptDispatcher.PrepareStart(scriptClient, spec);

        return new DispatchContext(request, scriptClient, endpoint, spec, start);
    }

    /// <summary>One dispatch: the request, the RAW agent client, the endpoint, the protocol-neutral spec and the dispatcher's prepared start call.</summary>
    private sealed record DispatchContext(ScriptExecutionRequest Request, IAsyncScriptService Client, ServiceEndPoint Endpoint, ScriptDispatchSpec Spec, Func<Task<ScriptStatusResponse>> Start);

    /// <summary>
    /// Dispatch a script to the agent and observe it to completion — or, on a
    /// resumed deployment, re-attach to a still-running script from a prior
    /// (crashed) run instead of launching a duplicate. The in-flight ScriptTicket
    /// is recorded <b>before</b> <c>StartScript</c> and cleared once observation
    /// completes, so a server crash (or a lost RPC response) anywhere from the
    /// moment the agent launches the script leaves a durable pointer the next run
    /// can re-attach to — never a duplicate dispatch.
    /// </summary>
    private async Task<ScriptExecutionResult> DispatchOrReattachAsync(DispatchContext dispatch, CancellationToken ct)
    {
        var request = dispatch.Request;

        // Scope the in-flight slot to the (machine, step, action) dispatch unit, NOT
        // the machine alone: a parallel batch can dispatch two scripts to one machine
        // at once (two StartWithPrevious steps sharing a target role), and a
        // machine-only key would make the second dispatch re-attach to the first's
        // ticket and skip its own script. Keyed by the stable, process-unique
        // step/action ids — display names carry no uniqueness guarantee.
        var slot = new DispatchSlot(request.Machine.Id, request.StepId, request.ActionId);

        // GetStatus / Cancel / Complete survive a brief dropped connection inside a time budget; every
        // exhausted budget rethrows the original exception, so the pause path below is unchanged and the
        // breaker still sees ONE failure per dispatch (retries never touch it). StartScript stays unretried.
        var scriptClient = new RetryingScriptClient(dispatch.Client, _rpcRetry, dispatch.Spec.Timeout, ct);

        var reattached = await TryReattachAsync(dispatch, scriptClient, ct).ConfigureAwait(false);
        if (reattached != null) return reattached;

        // Record-before-RPC: persist the ticket we are about to dispatch BEFORE
        // firing StartScript. If the server crashes (or the response is lost) in
        // the window where the agent has launched the script but the server has
        // not yet recorded the ticket, a resume would otherwise find no pointer and
        // dispatch a fresh ticket — running the (non-idempotent) script a SECOND
        // time. With the record first, resume always knows a ticket to re-probe:
        // TryReattachAsync re-attaches if the agent has it, and falls through to a
        // clean fresh dispatch (via the unknown-ticket answer) if the
        // script never actually started. The agent is idempotent for a known
        // ticket, so even a retried StartScript returns status, not a second run.
        if (_inFlightStore != null)
            await _inFlightStore.RecordDispatchedAsync(request.ServerTaskId, slot, dispatch.Spec.Ticket.TaskId, ct).ConfigureAwait(false);

        var startResponse = await dispatch.Start().ConfigureAwait(false);

        Log.Information("[Deploy] Dispatching script to agent {MachineName} with ticket {Ticket}",
            request.Machine.Name, dispatch.Spec.Ticket);

        var result = await _observer.ObserveAndCompleteAsync(request.Machine, scriptClient, dispatch.Spec.Ticket, dispatch.Spec.Timeout, ct, request.Masker, startResponse, dispatch.Endpoint, request.OutputSink, request.ShouldCancelRemoteScript).ConfigureAwait(false);

        // Clear the in-flight pointer ONLY on a definitive observation — i.e. the
        // observer RETURNED (script completed, reported a non-zero exit, or hit the
        // per-script timeout and was cancelled). On a THROW (a transient RPC drop
        // that outlived Halibut's retries, an unreachable agent, or cancellation)
        // the script may still be running on the agent, so we PRESERVE the pointer:
        // the deployment pauses and a resumed run re-attaches to the still-running
        // script instead of dispatching a duplicate.
        await ClearInFlightAsync(request.ServerTaskId, slot).ConfigureAwait(false);

        return result;
    }

    private async Task ClearInFlightAsync(int serverTaskId, DispatchSlot slot)
    {
        if (_inFlightStore != null)
            await _inFlightStore.ClearAsync(serverTaskId, slot, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// If a prior run recorded an in-flight ticket for this machine and the agent
    /// still has a usable script for it, observe that script to completion and
    /// return its result (no duplicate dispatch). Returns <see langword="null"/>
    /// — meaning "dispatch fresh" — when there is no record, the probe fails, or
    /// the agent no longer knows the ticket (<see cref="IScriptDispatcher.IsUnknownTicket"/>).
    /// The fresh-dispatch fallback is exactly today's behaviour, so re-attach can only
    /// ever AVOID a duplicate, never change a deployment that wasn't resuming.
    /// </summary>
    private async Task<ScriptExecutionResult> TryReattachAsync(DispatchContext dispatch, IAsyncScriptService scriptClient, CancellationToken ct)
    {
        if (_inFlightStore == null) return null;

        var request = dispatch.Request;

        // Same dispatch-unit scope as DispatchOrReattachAsync: only re-attach to a
        // ticket recorded for THIS (machine, step, action), never to a sibling step
        // concurrently dispatching to the same machine.
        var slot = new DispatchSlot(request.Machine.Id, request.StepId, request.ActionId);

        var existingTicketId = await _inFlightStore.TryGetTicketAsync(request.ServerTaskId, slot, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(existingTicketId)) return null;

        var existingTicket = new ScriptTicket(existingTicketId);

        ScriptStatusResponse probe;
        try
        {
            probe = await scriptClient.GetStatusAsync(new ScriptStatusRequest(existingTicket, 0)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A TRANSIENT probe failure (the agent is still unreachable on resume)
            // must NOT clear the pointer or dispatch fresh — the recorded script may
            // still be running, so a fresh dispatch would duplicate it. Preserve the
            // pointer and propagate so the deployment pauses again; a later resume
            // re-probes. Only a non-transient probe error falls back to a fresh
            // dispatch (today's behaviour).
            if (TransientFailureClassifier.IsTransient(ex))
            {
                Log.Information(ex, "[Deploy] Re-attach probe hit a transient failure for agent {MachineName} (ticket {Ticket}); preserving in-flight pointer for a later resume.",
                    request.Machine.Name, existingTicketId);
                throw;
            }

            Log.Information(ex, "[Deploy] Re-attach probe failed for agent {MachineName} (ticket {Ticket}); dispatching fresh.",
                request.Machine.Name, existingTicketId);
            await _inFlightStore.ClearAsync(request.ServerTaskId, slot, ct).ConfigureAwait(false);
            return null;
        }

        if (_scriptDispatcher.IsUnknownTicket(probe))
        {
            await _inFlightStore.ClearAsync(request.ServerTaskId, slot, ct).ConfigureAwait(false);
            return null;
        }

        Log.Information("[Deploy] Re-attaching to in-flight script on agent {MachineName} (ticket {Ticket}, state {State}) after resume — skipping duplicate dispatch.",
            request.Machine.Name, existingTicketId, probe.State);

        var result = await _observer.ObserveAndCompleteAsync(request.Machine, scriptClient, existingTicket, dispatch.Spec.Timeout, ct, request.Masker, probe, dispatch.Endpoint, request.OutputSink, request.ShouldCancelRemoteScript).ConfigureAwait(false);

        // Clear only on a definitive observation; preserve on a throw so a further
        // resume re-attaches again (same rationale as DispatchOrReattachAsync).
        await ClearInFlightAsync(request.ServerTaskId, slot).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Whether a <c>GetStatus</c> probe shows the agent still holds the script (V1 semantics: anything
    /// except <c>Complete + UnknownResult (-1)</c>, including <c>Pending</c>). Kept as the stable,
    /// test-pinned entry point; the rule itself lives with the protocol in
    /// <see cref="V1ScriptDispatcher.IsUnknownTicketProbe"/>.
    /// </summary>
    internal static bool AgentHasUsableScript(ScriptStatusResponse probe) => !V1ScriptDispatcher.IsUnknownTicketProbe(probe);

    private static ScriptFile[] BuildDirectScriptFiles(
        DeploymentFileCollection deploymentFiles,
        IReadOnlyList<PackageAcquisitionResult> packageReferences,
        byte[] variableBytes,
        byte[] sensitiveBytes,
        string password)
    {
        var files = deploymentFiles
            .Select(file => new ScriptFile(file.RelativePath, DataStream.FromBytes(file.Content), null))
            .ToList();

        files.AddRange(PackageReferenceScriptFileBridge.BuildScriptFiles(packageReferences));

        files.Add(new ScriptFile("variables.json", DataStream.FromBytes(variableBytes), null));

        if (!string.IsNullOrEmpty(password))
            files.Add(new ScriptFile("sensitiveVariables.json", DataStream.FromBytes(sensitiveBytes), password));

        return files.ToArray();
    }

    /// <summary>Stable, test-pinned entry point; the V1 wire mapping lives in <see cref="V1ScriptDispatcher"/>.</summary>
    internal static ScriptType MapSyntax(Message.Models.Deployments.Execution.ScriptSyntax syntax) => V1ScriptDispatcher.MapSyntax(syntax);

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
