using Squid.Message.Contracts.Tentacle;
using Serilog;

namespace Squid.Tentacle.ScriptExecution;

public partial class ScriptPodService
{
    private List<ProcessOutput> DrainLogs(ScriptPodContext ctx)
    {
        if (!ctx.StreamedLogLines.IsEmpty)
            return DrainStreamedLogs(ctx);

        var allLogs = _podManager.ReadPodLogs(ctx.PodName, ctx.LastLogTimestamp, ctx.Namespace);
        var logs = ExtractNewLogLines(ctx, allLogs, _kubernetesSettings.MaxLogBufferBytes);

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

    internal void StartLogStream(ScriptPodContext ctx)
    {
        if (_podOps == null) return;

        ctx.LogStreamCts = new CancellationTokenSource();
        ctx.LogStreamTask = Task.Run(() => StreamLogsAsync(ctx, ctx.LogStreamCts.Token));
    }

    private async Task StreamLogsAsync(ScriptPodContext ctx, CancellationToken ct)
    {
        try
        {
            using var stream = _podOps.ReadPodLogFollow(ctx.PodName, ctx.Namespace ?? _kubernetesSettings.TentacleNamespace, "script");
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
        var allLogs = _podManager.ReadPodLogs(ctx.PodName, targetNamespace: ctx.Namespace);
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
