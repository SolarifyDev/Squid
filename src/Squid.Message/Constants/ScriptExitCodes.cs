namespace Squid.Message.Constants;

public static class ScriptExitCodes
{
    public const int Success = 0;
    public const int UnknownResult = -1;
    public const int Fatal = -41;
    public const int PowerShellInvalid = -42;
    public const int Canceled = -43;
    public const int Timeout = -44;
    public const int ProcessTerminated = -45;
    public const int PodNotFound = -81;
    public const int ContainerTerminated = -82;
    public const int PodStartupFailed = -83;

    /// <summary>
    /// P1-T.11 (Phase-5 follow-up to 2026-04-24 audit): the agent rejected
    /// this dispatch because it was draining (graceful shutdown). The script
    /// did not run; the deployment should be retried (manually or, in a
    /// future server release, automatically). Distinct from
    /// <see cref="ProcessTerminated"/> which means the script started and was
    /// killed mid-run.
    ///
    /// <para>Numbered <c>-503</c> by analogy with HTTP 503 Service Unavailable
    /// — the resource is temporarily refusing requests but expected to come
    /// back. <see cref="IsInfrastructureFailure"/> includes this so existing
    /// classification logic treats drain rejections as infrastructure (not
    /// script-level) failures.</para>
    /// </summary>
    public const int AgentDraining = -503;

    public static bool IsInfrastructureFailure(int exitCode)
        => exitCode is Fatal or Timeout or PodNotFound or ContainerTerminated or ProcessTerminated or PodStartupFailed or AgentDraining;

    public static string Describe(int exitCode)
    {
        return exitCode switch
        {
            Success => "Success",
            1 => "General error",
            2 => "Misuse of shell builtin or invalid argument",
            126 => "Command found but not executable (permission denied)",
            127 => "Command not found — check that the required binary (helm, kubectl, etc.) is installed and in PATH",
            128 => "Invalid exit argument",
            >= 129 and <= 192 => $"Process killed by signal {exitCode - 128} (SIG{SignalName(exitCode - 128)})",
            UnknownResult => "Unknown result (ticket or process not found)",
            Fatal => "Fatal infrastructure failure",
            PowerShellInvalid => "Invalid PowerShell script",
            Canceled => "Script execution canceled",
            Timeout => "Script execution timed out",
            ProcessTerminated => "Process terminated unexpectedly",
            PodNotFound => "Kubernetes pod not found",
            ContainerTerminated => "Kubernetes container terminated unexpectedly",
            PodStartupFailed => "Kubernetes pod startup failed",
            AgentDraining => "Tentacle is shutting down and rejected this dispatch — retry expected",
            _ => $"Script exited with code {exitCode}"
        };
    }

    private static string SignalName(int signal)
    {
        return signal switch
        {
            1 => "HUP",
            2 => "INT",
            9 => "KILL",
            15 => "TERM",
            _ => signal.ToString()
        };
    }
}
