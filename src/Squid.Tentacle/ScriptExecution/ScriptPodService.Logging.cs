using Squid.Message.Contracts.Tentacle;
using Serilog;

namespace Squid.Tentacle.ScriptExecution;

public partial class ScriptPodService
{
    private List<ProcessOutput> DrainLogs(ScriptPodContext ctx)
    {
        var allLogs = _podManager.ReadPodLogs(ctx.PodName);

        return ExtractNewLogLines(ctx, allLogs);
    }

    private List<ProcessOutput> DrainFinalLogs(ScriptPodContext ctx)
    {
        var allLogs = _podManager.ReadPodLogs(ctx.PodName);

        return ExtractNewLogLines(ctx, allLogs);
    }

    private static List<ProcessOutput> ExtractNewLogLines(ScriptPodContext ctx, string allLogs)
    {
        if (string.IsNullOrEmpty(allLogs))
            return new List<ProcessOutput>();

        if (allLogs.Length < ctx.LastReadLogLength)
        {
            Log.Warning("Log truncation detected for ticket {TicketId} (expected ≥{Expected}, got {Actual}). Kubelet may have rotated logs.",
                ctx.TicketId, ctx.LastReadLogLength, allLogs.Length);

            ctx.LogTruncationDetected = true;
            ctx.LastReadLogLength = 0;
        }

        var newContent = allLogs.Length > ctx.LastReadLogLength
            ? allLogs[((int)ctx.LastReadLogLength)..]
            : string.Empty;

        ctx.LastReadLogLength = allLogs.Length;

        if (string.IsNullOrEmpty(newContent))
            return new List<ProcessOutput>();

        var lines = newContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var logs = new List<ProcessOutput>(lines.Length);

        foreach (var line in lines)
        {
            if (TryDetectEosMarker(ctx, line))
                continue;

            var parsed = PodLogLineParser.Parse(line);
            logs.Add(new ProcessOutput(parsed.Source, parsed.Text));
            ctx.LogSequence++;
        }

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
