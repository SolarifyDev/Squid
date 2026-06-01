using Squid.Message.Constants;

namespace Squid.Core.Services.DeploymentExecution.Script;

public class ScriptExecutionResult
{
    public bool Success { get; set; }
    public List<string> LogLines { get; set; } = new();
    public List<string> StderrLines { get; set; } = new();
    public int ExitCode { get; set; }

    /// <summary>
    /// True when output was streamed live to the task log as the script ran. The bulk
    /// post-completion persistence is then skipped to avoid duplicating those lines. Defaults to
    /// false, so any execution path that does not stream behaves exactly as before.
    /// </summary>
    public bool OutputStreamed { get; set; }

    public string BuildErrorSummary(int maxLines = 10)
    {
        var description = ScriptExitCodes.Describe(ExitCode);
        var stderr = StderrLines.Count > 0 ? StderrLines : LogLines;
        var tail = stderr.Count > maxLines ? stderr.Skip(stderr.Count - maxLines).ToList() : stderr;
        var summary = string.Join(Environment.NewLine, tail);

        if (string.IsNullOrWhiteSpace(summary))
            return $"{description} (exit code {ExitCode})";

        return $"{description} (exit code {ExitCode}):{Environment.NewLine}{summary}";
    }
}
