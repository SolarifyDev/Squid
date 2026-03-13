namespace Squid.Core.Services.DeploymentExecution.Script;

public class ScriptExecutionResult
{
    public bool Success { get; set; }
    public List<string> LogLines { get; set; } = new();
    public List<string> StderrLines { get; set; } = new();
    public int ExitCode { get; set; }

    public string BuildErrorSummary(int maxLines = 10)
    {
        var stderr = StderrLines.Count > 0 ? StderrLines : LogLines;
        var tail = stderr.Count > maxLines ? stderr.Skip(stderr.Count - maxLines).ToList() : stderr;
        var summary = string.Join(Environment.NewLine, tail);

        return string.IsNullOrWhiteSpace(summary)
            ? $"Script exited with code {ExitCode}"
            : $"Script exited with code {ExitCode}:{Environment.NewLine}{summary}";
    }
}
