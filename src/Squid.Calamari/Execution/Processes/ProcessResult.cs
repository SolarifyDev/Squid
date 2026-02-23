namespace Squid.Calamari.Execution.Processes;

public sealed class ProcessResult
{
    public ProcessResult(int exitCode)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }

    public bool Succeeded => ExitCode == 0;
}
