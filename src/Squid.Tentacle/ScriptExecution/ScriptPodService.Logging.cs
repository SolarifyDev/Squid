using Squid.Message.Contracts.Tentacle;
using Serilog;

namespace Squid.Tentacle.ScriptExecution;

public partial class ScriptPodService
{
    private List<ProcessOutput> DrainLogs(ScriptPodContext ctx)
    {
        if (!ctx.StreamedLogLines.IsEmpty)
            return DrainStreamedLogs(ctx);

        var allLogs = _podManager.ReadPodLogs(ctx.PodName, ctx.LastLogTimestamp);
        var logs = ExtractNewLogLines(ctx, allLogs, _kubernetesSettings.MaxLogBufferBytes);

        PersistLogTimestamp(ctx);
        DrainInjectedEvents(ctx, logs);

        return logs;
    }

    private List<ProcessOutput> DrainStreamedLogs(ScriptPodContext ctx)
    {
        var logs = new List<ProcessOutput>();

        while (ctx.StreamedLogLines.TryDequeue(out var line))
        {
            if (string.IsNullOrEmpty(line)) continue;

            if (TryDetectEosMarker(ctx, line)) continue;
            if (ctx.LogOutputTruncated) continue;

            var rawLine = line;

            if (PodLogEncryption.IsEncryptedLine(rawLine) && ctx.LogEncryptionKey != null)
            {
                var (success, plaintext) = PodLogEncryption.TryDecryptLine(rawLine, ctx.LogEncryptionKey);
                rawLine = success ? plaintext : rawLine;
            }

            var parsed = PodLogLineParser.Parse(rawLine);
            var text = SensitiveOutputMasker.MaskLine(parsed.Text, ctx.SensitiveValues);
            logs.Add(new ProcessOutput(parsed.Source, text));
            ctx.LogSequence++;
        }

        DrainInjectedEvents(ctx, logs);

        return logs;
    }

    /// <summary>
    /// Bound for <see cref="StopLogStream"/> — how long the teardown path
    /// will wait for the background log-stream task to acknowledge
    /// cancellation before logging a warning and moving on. 5 s is well
    /// above the kernel-level cancellation propagation cost (sub-ms) and
    /// any plausible Serilog flush latency, while staying short enough
    /// that a stuck stream task can't hold up <c>CompleteScript</c>.
    /// </summary>
    private static readonly TimeSpan LogStreamShutdownTimeout = TimeSpan.FromSeconds(5);

    internal void StartLogStream(ScriptPodContext ctx)
    {
        if (_podOps == null) return;

        ctx.LogStreamCts = new CancellationTokenSource();
        ctx.LogStreamTask = Task.Run(() => StreamLogsAsync(ctx, ctx.LogStreamCts.Token));
    }

    /// <summary>
    /// P0-4: cancel the log-stream CTS AND wait for the background task
    /// to actually finish before the caller proceeds with workspace
    /// cleanup. Replaces a bare <c>ctx.LogStreamCts?.Cancel()</c> that
    /// returned immediately and left the task running concurrently with
    /// the cleanup path.
    ///
    /// <para><b>Why this matters</b>: post-cancel the task is in the
    /// middle of <c>reader.ReadLineAsync(ct)</c> on a stream returned by
    /// <c>_podOps.ReadPodLogFollow</c>. Without a wait:</para>
    /// <list type="bullet">
    ///   <item>The task may still <c>StreamedLogLines.Enqueue</c> AFTER
    ///         <c>DrainFinalLogs</c> has already drained the queue — log
    ///         lines silently lost in the response.</item>
    ///   <item>The task may still read from a stream pointing at a pod
    ///         that <c>_podManager.DeletePod</c> has just removed —
    ///         throws inside the (now-stale) catch block, where the
    ///         exception goes to <c>TaskScheduler.UnobservedTaskException</c>
    ///         (NOT Serilog, since we don't subscribe to it).</item>
    ///   <item>Any pre-await synchronous throw (rare but possible) would
    ///         fault the task without the inner try/catch ever running.</item>
    /// </list>
    ///
    /// <para><b>Synchronous wait is intentional</b>: the only callers
    /// (<c>CompleteScript</c>, <c>CancelScript</c>) implement the
    /// synchronous <c>IScriptService</c> RPC contract — making them
    /// async would propagate up the Halibut surface and break
    /// compatibility with deployed servers. <c>task.Wait(timeout)</c>
    /// is acceptable here because we're already on a teardown path,
    /// not a hot loop, and we cap the wait at
    /// <see cref="LogStreamShutdownTimeout"/>.</para>
    /// </summary>
    internal static void StopLogStream(ScriptPodContext ctx)
    {
        var cts = ctx.LogStreamCts;
        var task = ctx.LogStreamTask;

        cts?.Cancel();

        if (task == null) return;

        try
        {
            if (!task.Wait(LogStreamShutdownTimeout))
            {
                Log.Warning(
                    "Log stream task did not finish within {Timeout}s after cancellation for ticket {TicketId}. " +
                    "Workspace cleanup will proceed; any subsequent log writes from the orphaned task will land on " +
                    "an already-disposed context and be observed by the inner try/catch.",
                    LogStreamShutdownTimeout.TotalSeconds, ctx.TicketId);
            }
        }
        catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // Expected: task.Wait wraps a single OCE in AggregateException
            // when the wait succeeded but the awaited task itself cancelled.
            // Same outcome as a clean exit — no warning needed.
        }
        catch (Exception ex)
        {
            // Any other escape — observe at Debug, matching the inner catch.
            // The most likely path is a Serilog flush failure inside the inner
            // catch propagating up, but we don't want to crash the script
            // teardown over a logging issue.
            Log.Debug(ex, "Log stream task raised after cancellation for ticket {TicketId}", ctx.TicketId);
        }
    }

    private async Task StreamLogsAsync(ScriptPodContext ctx, CancellationToken ct)
    {
        try
        {
            using var stream = _podOps.ReadPodLogFollow(ctx.PodName, _kubernetesSettings.TentacleNamespace, "script");
            using var reader = new StreamReader(stream);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;

                ctx.StreamedLogLines.Enqueue(line);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Debug(ex, "Log stream ended for ticket {TicketId}", ctx.TicketId);
        }
    }

    private List<ProcessOutput> DrainFinalLogs(ScriptPodContext ctx)
    {
        var allLogs = _podManager.ReadPodLogs(ctx.PodName);
        var logs = ExtractNewLogLines(ctx, allLogs, _kubernetesSettings.MaxLogBufferBytes);

        DrainInjectedEvents(ctx, logs);

        return logs;
    }

    private static void DrainInjectedEvents(ScriptPodContext ctx, List<ProcessOutput> logs)
    {
        while (ctx.InjectedEvents.TryDequeue(out var injected))
        {
            logs.Add(injected);
            ctx.LogSequence++;
        }
    }

    private static void PersistLogTimestamp(ScriptPodContext ctx)
    {
        try
        {
            var state = ScriptStateFile.TryRead(ctx.WorkDir);
            if (state == null) return;

            state.LastLogTimestamp = ctx.LastLogTimestamp;
            ScriptStateFile.Write(ctx.WorkDir, state);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to persist log timestamp for ticket {TicketId}", ctx.TicketId);
        }
    }

    internal static List<ProcessOutput> ExtractNewLogLines(ScriptPodContext ctx, string allLogs, long maxLogBufferBytes)
    {
        if (string.IsNullOrEmpty(allLogs))
            return new List<ProcessOutput>();

        var lines = allLogs.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var logs = new List<ProcessOutput>(lines.Length);
        var allLineHashes = new HashSet<int>(lines.Length);

        var newBytes = (long)System.Text.Encoding.UTF8.GetByteCount(allLogs);
        ctx.TotalBytesRead += newBytes;

        foreach (var line in lines)
        {
            var lineHash = line.GetHashCode(StringComparison.Ordinal);
            allLineHashes.Add(lineHash);

            if (ctx.RecentLineHashes.Contains(lineHash))
                continue;

            if (TryDetectEosMarker(ctx, line))
                continue;

            if (ctx.LogOutputTruncated)
                continue;

            if (maxLogBufferBytes > 0 && ctx.TotalBytesRead > maxLogBufferBytes)
            {
                ctx.LogOutputTruncated = true;

                Log.Warning("Log output truncated for ticket {TicketId} after {Bytes} bytes (limit: {Limit})",
                    ctx.TicketId, ctx.TotalBytesRead, maxLogBufferBytes);

                logs.Add(new ProcessOutput(ProcessOutputSource.StdErr,
                    $"[Warning] Log output truncated after {ctx.TotalBytesRead:N0} bytes (limit: {maxLogBufferBytes:N0} bytes). Output collection stopped but script continues running."));

                continue;
            }

            var rawLine = line;

            if (PodLogEncryption.IsEncryptedLine(rawLine) && ctx.LogEncryptionKey != null)
            {
                var (success, plaintext) = PodLogEncryption.TryDecryptLine(rawLine, ctx.LogEncryptionKey);
                rawLine = success ? plaintext : rawLine;
            }

            var parsed = PodLogLineParser.Parse(rawLine);
            var text = SensitiveOutputMasker.MaskLine(parsed.Text, ctx.SensitiveValues);
            logs.Add(new ProcessOutput(parsed.Source, text));
            ctx.LogSequence++;
        }

        if (ctx.RecentLineHashes.Count > 0 && allLineHashes.Count > 0 && !allLineHashes.Overlaps(ctx.RecentLineHashes))
            ctx.LogTruncationDetected = true;

        ctx.RecentLineHashes = allLineHashes;
        ctx.LastLogTimestamp = DateTime.UtcNow;

        return logs;
    }

    private static bool TryDetectEosMarker(ScriptPodContext ctx, string line)
    {
        if (ctx.EosDetected) return false;
        if (string.IsNullOrEmpty(ctx.EosMarkerToken)) return false;

        var result = EosMarker.TryParse(line, ctx.EosMarkerToken);

        if (result == null) return false;

        ctx.EosDetected = true;
        ctx.EosExitCode = result.ExitCode;

        Log.Debug("EOS marker detected for ticket {TicketId}, exit code {ExitCode}", ctx.TicketId, result.ExitCode);

        return true;
    }
}
