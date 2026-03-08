using Squid.Message.Contracts.Tentacle;

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
            var parsed = PodLogLineParser.Parse(line);
            logs.Add(new ProcessOutput(parsed.Source, parsed.Text));
            ctx.LogSequence++;
        }

        return logs;
    }
}
