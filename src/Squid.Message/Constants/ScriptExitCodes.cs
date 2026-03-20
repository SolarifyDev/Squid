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

    public static bool IsInfrastructureFailure(int exitCode)
        => exitCode is Fatal or Timeout or PodNotFound or ContainerTerminated or ProcessTerminated;

    public static string Describe(int exitCode)
    {
        return exitCode switch
        {
            Success => "Success",
            UnknownResult => "Unknown result (ticket or process not found)",
            Fatal => "Fatal infrastructure failure",
            PowerShellInvalid => "Invalid PowerShell script",
            Canceled => "Script execution canceled",
            Timeout => "Script execution timed out",
            ProcessTerminated => "Process terminated unexpectedly",
            PodNotFound => "Kubernetes pod not found",
            ContainerTerminated => "Kubernetes container terminated unexpectedly",
            _ => $"Script exited with code {exitCode}"
        };
    }
}
