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

    // Stable prefix of the truncation gap marker. Used both to render the marker
    // and to recognise a prior marker at the buffer head, so a re-truncation of
    // an otherwise-unchanged buffer doesn't emit a duplicate marker.
    internal const string TruncationMarkerPrefix = "[Squid] Older log lines were truncated here";

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
        ServiceEndPoint? endpoint = null,
        ScriptOutputSink outputSink = null)
    {
        var streamed = outputSink != null;
        var startTime = DateTime.UtcNow;
        var pollInterval = TimeSpan.FromMilliseconds(_observerSettings.InitialPollIntervalMs);
        var maxPollInterval = TimeSpan.FromMilliseconds(_observerSettings.MaxPollIntervalMs);
        var backoffFactor = _observerSettings.PollBackoffFactor;
        var statusResponse = initialStartResponse
            ?? new ScriptStatusResponse(ticket, ProcessState.Pending, 0, new List<ProcessOutput>(), 0);
        var allLogs = new List<ProcessOutput>();
        var streamFailed = false;

        // Live streaming is best-effort: on any sink failure we set streamFailed so the bulk persist
        // at completion runs as a fallback, guaranteeing lines are never lost (at-least-once; the
        // fallback may duplicate already-streamed lines, which is preferable to losing them).
        async Task StreamLinesAsync(IReadOnlyList<ScriptOutputLine> lines)
        {
            if (outputSink == null || lines == null || lines.Count == 0) return;

            try
            {
                await outputSink(lines, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                streamFailed = true;
                Log.Warning(ex, "[Deploy] Live log streaming failed for agent {MachineName}", machine.Name);
            }
        }

        Task StreamBatchAsync(List<ProcessOutput> batch)
        {
            if (outputSink == null || batch == null || batch.Count == 0) return Task.CompletedTask;

            var lines = batch
                .OrderBy(l => l.Occurred)
                .Where(l => !string.IsNullOrEmpty(l.Text))
                .Select(l => new ScriptOutputLine(l.Text, l.Source == ProcessOutputSource.StdErr))
                .ToList();

            return StreamLinesAsync(lines);
        }

        // Cap the buffer; if truncation produced a gap marker, stream it too so
        // it persists even when the streaming path skips the bulk persist (the
        // marker is also left in allLogs for the non-streaming bulk path — so it
        // lands exactly once in either mode).
        async Task TruncateAndStreamMarkerAsync()
        {
            var marker = TruncateIfExceeded(allLogs);
            if (marker != null)
                await StreamLinesAsync(new[] { new ScriptOutputLine(marker.Text, IsStdErr: false) }).ConfigureAwait(false);
        }

        if (initialStartResponse != null)
        {
            allLogs.AddRange(initialStartResponse.Logs);
            await TruncateAndStreamMarkerAsync().ConfigureAwait(false);
            LogOutput(initialStartResponse.Logs, machine.Name, masker);
            await StreamBatchAsync(initialStartResponse.Logs).ConfigureAwait(false);
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

                var timeoutLine = $"Script execution exceeded {scriptTimeout.TotalMinutes}-minute timeout";
                collectedLogLines.Add(timeoutLine);
                await StreamLinesAsync(new[] { new ScriptOutputLine(timeoutLine, IsStdErr: true) }).ConfigureAwait(false);

                return new ScriptExecutionResult
                {
                    Success = false,
                    ExitCode = ScriptExitCodes.Timeout,
                    LogLines = collectedLogLines,
                    OutputStreamed = streamed && !streamFailed
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
            await TruncateAndStreamMarkerAsync().ConfigureAwait(false);
            LogOutput(statusResponse.Logs, machine.Name, masker);
            await StreamBatchAsync(statusResponse.Logs).ConfigureAwait(false);

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
        await TruncateAndStreamMarkerAsync().ConfigureAwait(false);
        LogOutput(completeResponse.Logs, machine.Name, masker);
        await StreamBatchAsync(completeResponse.Logs).ConfigureAwait(false);

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
            ExitCode = completeResponse.ExitCode,
            OutputStreamed = streamed && !streamFailed
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

    /// <summary>
    /// Cap the in-memory log buffer for a single script. When truncation occurs,
    /// inserts an operator-visible gap marker at the head and RETURNS it so the
    /// caller can also stream it — without a marker the operator's log silently
    /// jumps (oldest lines vanish with only a Seq warning). Returns null when no
    /// truncation was needed.
    /// </summary>
    private ProcessOutput TruncateIfExceeded(List<ProcessOutput> logs)
    {
        var max = _observerSettings.MaxLogEntries > 0 ? _observerSettings.MaxLogEntries : DefaultMaxLogEntries;

        // A marker already at the head shouldn't itself count toward the cap —
        // otherwise it would trigger a redundant re-truncation (and a duplicate
        // marker) on the next call even when no real lines need dropping.
        var hasMarker = logs.Count > 0 && logs[0].Text.StartsWith(TruncationMarkerPrefix, StringComparison.Ordinal);
        var realCount = logs.Count - (hasMarker ? 1 : 0);

        if (realCount <= max) return null;

        if (hasMarker) logs.RemoveAt(0);   // drop the stale marker; a fresh one is re-inserted below

        var overflow = logs.Count - max;
        logs.RemoveRange(0, overflow);
        Log.Warning("[Deploy] Log buffer exceeded {Max} entries, truncated {Overflow} oldest entries", max, overflow);

        // Anchor the marker to the oldest-retained entry's timestamp so the
        // stable Occurred-ordering keeps it just ahead of that entry (i.e. at
        // the head of the final log). Source = StdOut so it surfaces as an
        // informational notice, never a false error signal.
        var anchor = logs.Count > 0 ? logs[0].Occurred : DateTimeOffset.UtcNow;
        var marker = new ProcessOutput(
            ProcessOutputSource.StdOut,
            $"{TruncationMarkerPrefix} - this step's output exceeded the {max}-line retention buffer and the oldest lines were dropped.",
            anchor);
        logs.Insert(0, marker);
        return marker;
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
