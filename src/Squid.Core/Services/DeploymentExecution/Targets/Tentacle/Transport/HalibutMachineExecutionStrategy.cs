using System.Runtime.ExceptionServices;
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
    private readonly IInFlightScriptStore _inFlightStore;
    private readonly RpcRetryHandler _rpcRetry;
    private readonly IScriptDispatcherSelector _dispatcherSelector;
    private readonly IMachineCapabilitiesRefresher _capabilitiesRefresher;
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
        IScriptDispatcherSelector dispatcherSelector = null,
        IMachineCapabilitiesRefresher capabilitiesRefresher = null)
    {
        _payloadBuilder = payloadBuilder;
        _observer = observer;
        _breakerRegistry = breakerRegistry;
        _inFlightStore = inFlightStore;
        _capabilitiesRefresher = capabilitiesRefresher;
        _rpcRetry = rpcRetry ?? new RpcRetryHandler(halibutSetting.RpcRetry);

        // Composed without a selector (tests, V1-only wiring): V1 is the only dispatcher, which is also what
        // every cold-cache machine gets. Production injects the selector with every registered dispatcher.
        _dispatcherSelector = dispatcherSelector ?? new ScriptDispatcherSelector(new IScriptDispatcher[] { new V1ScriptDispatcher(halibutClientFactory) }, capabilitiesCache);
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

        // Adopt a capabilities snapshot another pod (or an upgrade/downgrade) wrote after this pod cached its own,
        // so the protocol version chosen below reflects what the agent speaks NOW, not what this pod last saw.
        if (_capabilitiesRefresher != null)
            await _capabilitiesRefresher.SyncFromStoreAsync(request.Machine.Id, ct).ConfigureAwait(false);

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
            try
            {
                ScriptExecutionResult result;
                if (plan is PackagedPayloadExecutionPlan packagedPlan)
                    result = await ExecuteCalamariViaHalibutAsync(packagedPlan, endpoint, ct).ConfigureAwait(false);
                else
                    result = await ExecuteDirectScriptViaHalibutAsync((DirectScriptExecutionPlan)plan, endpoint, ct).ConfigureAwait(false);

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
        PackagedPayloadExecutionPlan plan, ServiceEndPoint endpoint, CancellationToken ct)
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

        var dispatch = BuildDispatch(request, endpoint, scriptBody, scriptFiles, ct);

        return await DispatchOrReattachAsync(dispatch, ct).ConfigureAwait(false);
    }

    private async Task<ScriptExecutionResult> ExecuteDirectScriptViaHalibutAsync(
        DirectScriptExecutionPlan plan, ServiceEndPoint endpoint, CancellationToken ct)
    {
        var request = plan.Request;
        var (variableBytes, sensitiveBytes, password) =
            ScriptExecutionHelper.CreateVariableFileContents(request.Variables);

        var scriptFiles = BuildDirectScriptFiles(request.DeploymentFiles, request.PackageReferences, variableBytes, sensitiveBytes, password);
        var dispatch = BuildDispatch(request, endpoint, request.ScriptBody, scriptFiles, ct);

        return await DispatchOrReattachAsync(dispatch, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Everything one dispatch needs, protocol-neutral: the ticket / timeout / mutex name are decided here
    /// (shared by every <see cref="IScriptDispatcher"/>), the dispatcher is chosen from what the agent
    /// ADVERTISED, and its session validates the spec and prepares its start call — before any in-flight
    /// record or agent probe.
    /// </summary>
    private DispatchContext BuildDispatch(ScriptExecutionRequest request, ServiceEndPoint endpoint, string scriptBody, ScriptFile[] scriptFiles, CancellationToken ct)
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

        var dispatcher = _dispatcherSelector.ForMachine(request.Machine.Id);
        var context = new ScriptSessionContext(_rpcRetry, spec.Timeout, ct, _observer);
        var session = dispatcher.Open(endpoint, context);
        var start = session.PrepareStart(spec);

        Log.Debug("[Deploy] Machine {MachineName} dispatching via script service {ProtocolVersion}", request.Machine.Name, session.Version);

        return new DispatchContext(request, endpoint, spec, context, session, start);
    }

    /// <summary>One dispatch: the request, the endpoint, the protocol-neutral spec, the session opened for the chosen version and its prepared start call.</summary>
    private sealed record DispatchContext(ScriptExecutionRequest Request, ServiceEndPoint Endpoint, ScriptDispatchSpec Spec, ScriptSessionContext Context, IScriptAgentSession Session, Func<Task<AgentScriptStatus>> Start);

    /// <summary>A ticket a prior run recorded, with the protocol version it was dispatched with.</summary>
    private sealed record RecordedDispatch(string TicketId, ScriptProtocolVersion Version);

    /// <summary>
    /// Scope the in-flight slot to the (machine, step, action) dispatch unit, NOT the machine alone: a
    /// parallel batch can dispatch two scripts to one machine at once (two StartWithPrevious steps sharing
    /// a target role), and a machine-only key would make the second dispatch re-attach to the first's
    /// ticket and skip its own script. Keyed by the stable, process-unique step/action ids — display names
    /// carry no uniqueness guarantee.
    /// </summary>
    private static DispatchSlot SlotFor(ScriptExecutionRequest request) => new(request.Machine.Id, request.StepId, request.ActionId);

    /// <summary>
    /// Dispatch a script to the agent and observe it to completion — or, on a
    /// resumed deployment, re-attach to a still-running script from a prior
    /// (crashed) run instead of launching a duplicate. The in-flight ScriptTicket
    /// (with the protocol version it is dispatched with) is recorded <b>before</b>
    /// <c>StartScript</c> and cleared once observation completes, so a server crash
    /// (or a lost RPC response) anywhere from the moment the agent launches the
    /// script leaves a durable pointer the next run can re-attach to — never a
    /// duplicate dispatch.
    /// </summary>
    private async Task<ScriptExecutionResult> DispatchOrReattachAsync(DispatchContext dispatch, CancellationToken ct)
    {
        var request = dispatch.Request;
        var slot = SlotFor(request);

        var reattached = await TryReattachAsync(dispatch, slot, ct).ConfigureAwait(false);
        if (reattached != null) return reattached;

        // Record-before-RPC: persist the ticket we are about to dispatch BEFORE
        // firing StartScript. If the server crashes (or the response is lost) in
        // the window where the agent has launched the script but the server has
        // not yet recorded the ticket, a resume would otherwise find no pointer and
        // dispatch a fresh ticket — running the (non-idempotent) script a SECOND
        // time. With the record first, resume always knows a ticket to re-probe:
        // TryReattachAsync re-attaches if the agent has it, and falls through to a
        // clean fresh dispatch (via the unknown-ticket answer) if the
        // script never actually started. The version is part of the same write, so a
        // resume probes in the dialect the script was started with. The agent is
        // idempotent for a known ticket, so even a retried StartScript returns status,
        // not a second run.
        await RecordDispatchAsync(dispatch, slot, ct).ConfigureAwait(false);

        var started = await StartWithProtocolFallbackAsync(dispatch, slot, ct).ConfigureAwait(false);

        Log.Information("[Deploy] Dispatching script to agent {MachineName} with ticket {Ticket}",
            request.Machine.Name, dispatch.Spec.Ticket);

        var result = await started.Dispatch.Session.ObserveAsync(request, dispatch.Endpoint, dispatch.Spec.Ticket, started.Status).ConfigureAwait(false);

        // Clear the in-flight pointer ONLY on a definitive observation — i.e. the
        // observer RETURNED (script completed, reported a non-zero exit, hit the
        // per-script timeout and was cancelled, or the agent reported the outcome lost).
        // On a THROW (a transient RPC drop that outlived Halibut's retries, an unreachable
        // agent, or cancellation) the script may still be running on the agent, so we
        // PRESERVE the pointer: the deployment pauses and a resumed run re-attaches to
        // the still-running script instead of dispatching a duplicate.
        await ClearInFlightAsync(request.ServerTaskId, slot).ConfigureAwait(false);

        return result;
    }

    private async Task RecordDispatchAsync(DispatchContext dispatch, DispatchSlot slot, CancellationToken ct)
    {
        if (_inFlightStore != null)
            await _inFlightStore.RecordDispatchedAsync(dispatch.Request.ServerTaskId, slot, dispatch.Spec.Ticket.TaskId, dispatch.Session.Version, ct).ConfigureAwait(false);
    }

    private sealed record StartedDispatch(DispatchContext Dispatch, AgentScriptStatus Status);

    /// <summary>
    /// Starts the script over the chosen version. A V2 Start that fails because the agent no longer speaks V2
    /// (it was downgraded, or this pod's cache is stale — a V1-only agent answers a V2 call with a plain
    /// transport error the classifier treats as a blip) must not pause the deployment forever: the agent is
    /// asked what it advertises, and only if it has withdrawn V2 is the script dispatched over V1 instead.
    /// The V2 Start cannot have reached an agent that does not serve V2, so the fresh dispatch is safe;
    /// any other failure — including an unreachable agent — propagates unchanged and is charged to the breaker.
    /// </summary>
    private async Task<StartedDispatch> StartWithProtocolFallbackAsync(DispatchContext dispatch, DispatchSlot slot, CancellationToken ct)
    {
        try
        {
            return new StartedDispatch(dispatch, await dispatch.Start().ConfigureAwait(false));
        }
        catch (Exception ex) when (IsPossibleProtocolMismatch(dispatch.Session, ex))
        {
            if (!await AgentWithdrewV2Async(dispatch, ct).ConfigureAwait(false)) throw;

            Log.Warning(ex, "[Deploy] Agent {MachineName} no longer advertises script service V2; dispatching ticket {Ticket} over V1", dispatch.Request.Machine.Name, dispatch.Spec.Ticket.TaskId);

            var v1 = SwitchToV1(dispatch);

            await RecordDispatchAsync(v1, slot, ct).ConfigureAwait(false);

            return new StartedDispatch(v1, await v1.Start().ConfigureAwait(false));
        }
    }

    /// <summary>A V2 call that failed in a way a V1-only agent would also produce: a transport blip, or the service/method being absent.</summary>
    private static bool IsPossibleProtocolMismatch(IScriptAgentSession session, Exception ex)
        => session.Version == ScriptProtocolVersion.V2 && (TransientFailureClassifier.IsTransient(ex) || TransientFailureClassifier.IsServiceMissing(ex));

    /// <summary>True only when the agent ANSWERED a capabilities call and no longer lists V2; an unanswered call proves nothing.</summary>
    private async Task<bool> AgentWithdrewV2Async(DispatchContext dispatch, CancellationToken ct)
    {
        if (_capabilitiesRefresher == null) return false;

        var capabilities = await _capabilitiesRefresher.RefreshFromAgentAsync(dispatch.Request.Machine.Id, dispatch.Endpoint, ct).ConfigureAwait(false);

        return capabilities != null && !capabilities.SupportsScriptServiceV2;
    }

    private DispatchContext SwitchToV1(DispatchContext dispatch)
    {
        var session = _dispatcherSelector.ForVersion(ScriptProtocolVersion.V1).Open(dispatch.Endpoint, dispatch.Context);

        return dispatch with { Session = session, Start = session.PrepareStart(dispatch.Spec) };
    }

    private async Task ClearInFlightAsync(int serverTaskId, DispatchSlot slot)
    {
        if (_inFlightStore != null)
            await _inFlightStore.ClearAsync(serverTaskId, slot, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// If a prior run recorded an in-flight ticket for this dispatch unit and the agent still has a usable
    /// script for it, observe that script to completion and return its result (no duplicate dispatch).
    /// The probe and the observation use the protocol version the script was dispatched with, not the one
    /// the agent advertises now. Returns <see langword="null"/> — meaning "dispatch fresh" — when there is
    /// no record, a V1 probe fails non-transiently, or the agent says it never had the ticket
    /// (<see cref="IScriptAgentSession.IsUnknownTicket"/>). A script whose result the agent lost fails the
    /// action instead (<see cref="LostScriptResult"/>): it may have run, so it must not run again.
    /// </summary>
    private async Task<ScriptExecutionResult> TryReattachAsync(DispatchContext dispatch, DispatchSlot slot, CancellationToken ct)
    {
        if (_inFlightStore == null) return null;

        var request = dispatch.Request;
        var recorded = await ReadRecordedDispatchAsync(request.ServerTaskId, slot, ct).ConfigureAwait(false);

        if (recorded == null) return null;

        var existingTicket = new ScriptTicket(recorded.TicketId);
        var session = OpenSessionFor(dispatch, recorded.Version);
        var probed = await ProbeRecordedTicketAsync(dispatch, session, existingTicket, ct).ConfigureAwait(false);
        var probe = probed.Status;

        session = probed.Session;

        // A V2 ticket asked about over V1 (the agent was downgraded since): an unreleased ticket is visible to V1, so
        // "unknown" can only mean the agent released it or lost it — it may have run, so the action fails instead of re-running.
        if (probed.OverV1AfterDowngrade && session.IsUnknownTicket(probe))
            return await FailLostResultAsync(request, existingTicket, slot).ConfigureAwait(false);

        if (probe == null || session.IsUnknownTicket(probe))
        {
            await _inFlightStore.ClearAsync(request.ServerTaskId, slot, ct).ConfigureAwait(false);
            return null;
        }

        if (probe.State == AgentScriptState.ResultLost)
            return await FailLostResultAsync(request, existingTicket, slot).ConfigureAwait(false);

        Log.Information("[Deploy] Re-attaching to in-flight script on agent {MachineName} (ticket {Ticket}, protocol {ProtocolVersion}, state {State}) after resume — skipping duplicate dispatch.",
            request.Machine.Name, recorded.TicketId, session.Version, probe.State);

        var result = await session.ObserveAsync(request, dispatch.Endpoint, existingTicket, probe).ConfigureAwait(false);

        // Clear only on a definitive observation; preserve on a throw so a further
        // resume re-attaches again (same rationale as DispatchOrReattachAsync).
        await ClearInFlightAsync(request.ServerTaskId, slot).ConfigureAwait(false);

        return result;
    }

    private async Task<RecordedDispatch> ReadRecordedDispatchAsync(int serverTaskId, DispatchSlot slot, CancellationToken ct)
    {
        var ticketId = await _inFlightStore.TryGetTicketAsync(serverTaskId, slot, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(ticketId)) return null;

        var version = await _inFlightStore.TryGetProtocolAsync(serverTaskId, slot, ct).ConfigureAwait(false);

        return new RecordedDispatch(ticketId, version ?? ScriptProtocolVersion.V1);
    }

    /// <summary>
    /// The session for the version a script was recorded with: the one already opened for this dispatch when
    /// it is the same version, otherwise the recorded version's dispatcher. The recorded version always
    /// wins — even if the agent no longer advertises it (rolled back, or this pod's cache is stale): the
    /// script lives in the agent's V2 state, V1 cannot ask about it, and mapping a V1 "unknown" onto it
    /// would re-run it. If the agent really cannot answer, the probe throws and the pointer is preserved.
    /// </summary>
    private IScriptAgentSession OpenSessionFor(DispatchContext dispatch, ScriptProtocolVersion recordedVersion)
    {
        if (recordedVersion == dispatch.Session.Version) return dispatch.Session;

        if (!_dispatcherSelector.Advertises(dispatch.Request.Machine.Id, recordedVersion))
            Log.Warning("[Deploy] Script on agent {MachineName} was dispatched with protocol {ProtocolVersion}, which the agent does not currently advertise; re-attaching with that version anyway so the script is never re-run.", dispatch.Request.Machine.Name, recordedVersion);

        return _dispatcherSelector.ForVersion(recordedVersion).Open(dispatch.Endpoint, dispatch.Context);
    }

    private sealed record RecordedProbe(IScriptAgentSession Session, AgentScriptStatus Status, bool OverV1AfterDowngrade);

    /// <summary>
    /// Probes the recorded ticket. Returns a null status — "dispatch fresh" — only for a non-transient
    /// failure of a protocol that has no explicit unknown answer (V1). A TRANSIENT probe failure (the agent is
    /// still unreachable on resume) must NOT clear the pointer or dispatch fresh — the recorded script may still
    /// be running, so a fresh dispatch would duplicate it: the pointer is preserved and the exception propagates
    /// so the deployment pauses again. The one exception: a V2-recorded ticket whose probe fails while the agent
    /// ANSWERS that it no longer advertises V2 (downgraded) is probed over V1 instead, so the resume neither
    /// pauses forever nor re-runs the script.
    /// </summary>
    private async Task<RecordedProbe> ProbeRecordedTicketAsync(DispatchContext dispatch, IScriptAgentSession session, ScriptTicket ticket, CancellationToken ct)
    {
        try
        {
            return new RecordedProbe(session, await session.ProbeAsync(ticket).ConfigureAwait(false), false);
        }
        catch (Exception ex)
        {
            if (!IsPossibleProtocolMismatch(session, ex) || !await AgentWithdrewV2Async(dispatch, ct).ConfigureAwait(false))
                return HandleProbeFailure(dispatch.Request, session, ticket, ex);

            Log.Warning(ex, "[Deploy] Agent {MachineName} no longer advertises script service V2; probing V2 ticket {Ticket} over V1", dispatch.Request.Machine.Name, ticket.TaskId);

            var v1 = SwitchToV1(dispatch).Session;

            return new RecordedProbe(v1, await v1.ProbeAsync(ticket).ConfigureAwait(false), true);
        }
    }

    private static RecordedProbe HandleProbeFailure(ScriptExecutionRequest request, IScriptAgentSession session, ScriptTicket ticket, Exception ex)
    {
        if (TransientFailureClassifier.IsTransient(ex))
        {
            Log.Information(ex, "[Deploy] Re-attach probe hit a transient failure for agent {MachineName} (ticket {Ticket}); preserving in-flight pointer for a later resume.",
                request.Machine.Name, ticket.TaskId);
            ExceptionDispatchInfo.Capture(ex).Throw();
        }

        if (!session.ProbeFailureMeansUnknownTicket)
        {
            Log.Warning(ex, "[Deploy] Re-attach probe failed for agent {MachineName} (ticket {Ticket}, protocol {ProtocolVersion}); the protocol answers unknown explicitly, so this is not evidence the script did not run — preserving the in-flight pointer.",
                request.Machine.Name, ticket.TaskId, session.Version);
            ExceptionDispatchInfo.Capture(ex).Throw();
        }

        Log.Information(ex, "[Deploy] Re-attach probe failed for agent {MachineName} (ticket {Ticket}); dispatching fresh.",
            request.Machine.Name, ticket.TaskId);

        return new RecordedProbe(session, null, false);
    }

    private async Task<ScriptExecutionResult> FailLostResultAsync(ScriptExecutionRequest request, ScriptTicket ticket, DispatchSlot slot)
    {
        Log.Error("[Deploy] Agent {MachineName} lost the result of script {Ticket}; failing the action instead of re-running it", request.Machine.Name, ticket.TaskId);

        var lost = LostScriptResult.Build(AgentScriptState.ResultLost, request.Machine.Name, ticket, new List<string>());

        await ClearInFlightAsync(request.ServerTaskId, slot).ConfigureAwait(false);

        return lost;
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
