using Halibut;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Settings.Halibut;
using Squid.Message.Constants;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public sealed class HalibutScriptObserver : IHalibutScriptObserver
{
    internal const int DefaultMaxLogEntries = 100_000;

    private readonly ObserverSettings _observerSettings;
    private readonly LivenessSettings _livenessSettings;
    private readonly IAgentLivenessProbe? _livenessProbe;

    public HalibutScriptObserver() : this(new ObserverSettings(), new LivenessSettings(), livenessProbe: null) { }

    public HalibutScriptObserver(HalibutSetting halibutSetting, IAgentLivenessProbe livenessProbe = null)
        : this(halibutSetting?.Observer ?? new ObserverSettings(),
               halibutSetting?.Liveness ?? new LivenessSettings(),
               livenessProbe)
    { }

    public HalibutScriptObserver(ObserverSettings observerSettings)
        : this(observerSettings, new LivenessSettings(), livenessProbe: null) { }

    public HalibutScriptObserver(ObserverSettings observerSettings, LivenessSettings livenessSettings, IAgentLivenessProbe? livenessProbe)
    {
        _observerSettings = observerSettings ?? new ObserverSettings();
        _livenessSettings = livenessSettings ?? new LivenessSettings();
        _livenessProbe = livenessProbe;
    }

    // NOTE: Halibut RPC proxy calls (GetStatusAsync, CompleteScriptAsync, CancelScriptAsync)
    // do not accept CancellationToken — cancellation is only checked between polling intervals.
    // This is a known Halibut 8.x limitation.
    public async Task<ScriptExecutionResult> ObserveAndCompleteAsync(
        Machine machine,
        IAsyncScriptService scriptClient,
        ScriptTicket ticket,
        TimeSpan scriptTimeout,
        CancellationToken ct,
        SensitiveValueMasker masker = null,
        ScriptStatusResponse initialStartResponse = null,
        ServiceEndPoint? endpoint = null)
    {
        var startTime = DateTime.UtcNow;
        var pollInterval = TimeSpan.FromMilliseconds(_observerSettings.InitialPollIntervalMs);
        var maxPollInterval = TimeSpan.FromMilliseconds(_observerSettings.MaxPollIntervalMs);
        var backoffFactor = _observerSettings.PollBackoffFactor;
        var statusResponse = initialStartResponse
            ?? new ScriptStatusResponse(ticket, ProcessState.Pending, 0, new List<ProcessOutput>(), 0);
        var allLogs = new List<ProcessOutput>();

        if (initialStartResponse != null)
        {
            allLogs.AddRange(initialStartResponse.Logs);
            TruncateIfExceeded(allLogs);
            LogOutput(initialStartResponse.Logs, machine.Name, masker);
        }

        // Agent liveness probe: independent probing stream alongside the main polling
        // loop. If the agent stops responding to capabilities probes N times in a row,
        // we abort the wait with AgentUnreachableException rather than sit for the
        // full scriptTimeout (which can be 30min+) waiting for GetStatusAsync to
        // eventually time out at the Halibut layer. Only started when a probe and
        // endpoint are both available — null-safe for tests and pre-wiring callers.
        using var livenessCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var livenessTask = StartLivenessProbeLoop(machine, endpoint, livenessCts);

        while (statusResponse.State != ProcessState.Complete)
        {
            if (DateTime.UtcNow - startTime > scriptTimeout)
            {
                Log.Warning("[Deploy] Script execution timeout ({TimeoutMinutes}m) on agent {MachineName}, cancelling",
                    scriptTimeout.TotalMinutes, machine.Name);
                await TryCancelScriptAsync(scriptClient, ticket, statusResponse.NextLogSequence).ConfigureAwait(false);

                var collectedLogLines = allLogs
                    .OrderBy(l => l.Occurred)
                    .Where(l => !string.IsNullOrEmpty(l.Text))
                    .Select(l => masker?.Mask(l.Text) ?? l.Text)
                    .ToList();

                collectedLogLines.Add($"Script execution exceeded {scriptTimeout.TotalMinutes}-minute timeout");

                return new ScriptExecutionResult
                {
                    Success = false,
                    ExitCode = ScriptExitCodes.Timeout,
                    LogLines = collectedLogLines
                };
            }

            try
            {
                ct.ThrowIfCancellationRequested();

                // If the background liveness loop raised AgentUnreachableException
                // it will have faulted; surface that here as a transient failure
                // rather than waiting the full scriptTimeout.
                if (livenessTask.IsFaulted)
                {
                    var unreachable = livenessTask.Exception?.Flatten().InnerExceptions
                        .OfType<AgentUnreachableException>().FirstOrDefault();
                    if (unreachable != null)
                    {
                        Log.Warning("[Deploy] Agent {MachineName} unreachable after {Failures} consecutive probe failures — aborting wait",
                            machine.Name, unreachable.ConsecutiveFailures);
                        await TryCancelScriptAsync(scriptClient, ticket, statusResponse.NextLogSequence).ConfigureAwait(false);
                        throw unreachable;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await TryCancelScriptAsync(scriptClient, ticket, statusResponse.NextLogSequence).ConfigureAwait(false);
                throw;
            }

            statusResponse = await scriptClient.GetStatusAsync(
                new ScriptStatusRequest(ticket, statusResponse.NextLogSequence)).ConfigureAwait(false);

            allLogs.AddRange(statusResponse.Logs);
            TruncateIfExceeded(allLogs);
            LogOutput(statusResponse.Logs, machine.Name, masker);

            if (statusResponse.State != ProcessState.Complete)
            {
                try
                {
                    await Task.Delay(pollInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await TryCancelScriptAsync(scriptClient, ticket, statusResponse.NextLogSequence).ConfigureAwait(false);
                    throw;
                }

                pollInterval = TimeSpan.FromMilliseconds(Math.Min(pollInterval.TotalMilliseconds * backoffFactor, maxPollInterval.TotalMilliseconds));
            }
        }

        // Script finished normally — stop the liveness probe loop.
        livenessCts.Cancel();
        try { await livenessTask.ConfigureAwait(false); }
        catch { /* probe loop swallows its own exceptions into the task result */ }

        var completeResponse = await scriptClient.CompleteScriptAsync(
            new CompleteScriptCommand(ticket, statusResponse.NextLogSequence)).ConfigureAwait(false);

        allLogs.AddRange(completeResponse.Logs);
        TruncateIfExceeded(allLogs);
        LogOutput(completeResponse.Logs, machine.Name, masker);

        var orderedLogs = allLogs
            .OrderBy(l => l.Occurred)
            .Where(l => !string.IsNullOrEmpty(l.Text))
            .ToList();

        var logLines = orderedLogs.Select(l => masker?.Mask(l.Text) ?? l.Text).ToList();

        var stderrLines = orderedLogs
            .Where(l => l.Source == ProcessOutputSource.StdErr)
            .Select(l => masker?.Mask(l.Text) ?? l.Text)
            .ToList();

        var success = completeResponse.ExitCode == 0;

        if (!success)
            Log.Error("[Deploy] Script failed on agent {MachineName} with exit code {ExitCode}",
                machine.Name, completeResponse.ExitCode);
        else
            Log.Information("[Deploy] Script completed successfully on agent {MachineName}", machine.Name);

        return new ScriptExecutionResult
        {
            Success = success,
            LogLines = logLines,
            StderrLines = stderrLines,
            ExitCode = completeResponse.ExitCode
        };
    }

    private Task StartLivenessProbeLoop(Machine machine, ServiceEndPoint? endpoint, CancellationTokenSource linkedCts)
    {
        if (_livenessProbe == null || endpoint == null)
            return Task.CompletedTask;

        var probeInterval = TimeSpan.FromSeconds(Math.Max(1, _livenessSettings.ProbeIntervalSeconds));
        var probeTimeout = TimeSpan.FromSeconds(Math.Max(1, _livenessSettings.ProbeTimeoutSeconds));
        var threshold = Math.Max(1, _livenessSettings.FailureThreshold);

        return Task.Run(async () =>
        {
            var consecutiveFailures = 0;
            while (!linkedCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(probeInterval, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }

                if (linkedCts.IsCancellationRequested) return;

                bool alive;
                try
                {
                    alive = await _livenessProbe.ProbeAsync(endpoint, probeTimeout, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linkedCts.IsCancellationRequested) { return; }
                catch { alive = false; }

                if (alive)
                {
                    consecutiveFailures = 0;
                    continue;
                }

                consecutiveFailures++;
                Log.Debug("[Deploy] Liveness probe failed for {MachineName} ({Count}/{Threshold})",
                    machine.Name, consecutiveFailures, threshold);

                if (consecutiveFailures >= threshold)
                    throw new AgentUnreachableException(machine.Name, consecutiveFailures);
            }
        }, linkedCts.Token);
    }

    private static void LogOutput(List<ProcessOutput> logs, string machineName, SensitiveValueMasker masker)
    {
        foreach (var log in logs)
            Log.Information("[Deploy:Agent] Machine={MachineName}, Source={Source}, Message={Message}",
                machineName, log.Source, masker?.Mask(log.Text) ?? log.Text);
    }

    private void TruncateIfExceeded(List<ProcessOutput> logs)
    {
        var max = _observerSettings.MaxLogEntries > 0 ? _observerSettings.MaxLogEntries : DefaultMaxLogEntries;
        if (logs.Count <= max) return;

        var overflow = logs.Count - max;
        logs.RemoveRange(0, overflow);
        Log.Warning("[Deploy] Log buffer exceeded {Max} entries, truncated {Overflow} oldest entries", max, overflow);
    }

    private static async Task TryCancelScriptAsync(
        IAsyncScriptService scriptClient, ScriptTicket ticket, long lastLogSequence)
    {
        try
        {
            await scriptClient.CancelScriptAsync(
                new CancelScriptCommand(ticket, lastLogSequence)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Deploy] Failed to cancel script with ticket {Ticket}", ticket.TaskId);
        }
    }
}
