using System.ServiceProcess;
using Microsoft.Extensions.Hosting;

namespace Squid.Tentacle.Core;

/// <summary>
/// Maps the agent's exit outcome onto the exit code reported to the Windows SCM.
/// <c>WindowsServiceLifetime</c> never sets <see cref="ServiceBase.ExitCode"/>, and neither
/// <c>Main</c>'s return value nor <c>Environment.ExitCode</c> reaches the service status, so
/// without this a fatal agent error is a clean SERVICE_STOPPED (code 0) and the configured
/// recovery actions never fire.
/// </summary>
public static class ScmExitCode
{
    /// <summary>A stop requested by SCM / the host (operator <c>sc stop</c>, shutdown) is always a clean 0;
    /// otherwise the pipeline's exit code is reported as-is (non-zero = failure = recovery applies).</summary>
    public static int Resolve(int pipelineExitCode, bool stopRequested) => stopRequested || IsPermanentFailure(pipelineExitCode) ? 0 : pipelineExitCode;

    /// <summary>A failure no restart can fix (registration permission denied): reported to the SCM as a clean stop so
    /// the restart actions do not re-register against the server every 10s forever. The agent has already logged the
    /// operator hint (and the SCM diagnostic log); the service simply stays STOPPED. Transient failures keep a
    /// non-zero code so recovery restarts them.</summary>
    public static bool IsPermanentFailure(int pipelineExitCode) => pipelineExitCode == TentacleEntry.PermissionDeniedExitCode;

    /// <summary>Returns a reporter that writes the code to the SCM-bound lifetime; a no-op when the
    /// lifetime is not a Windows service (Linux / console).</summary>
    public static Action<int> ReporterFor(IHostLifetime lifetime) => code => Report(lifetime, code);

    private static void Report(IHostLifetime lifetime, int code)
    {
        if (!OperatingSystem.IsWindows() || lifetime is not ServiceBase service) return;

        service.ExitCode = code;
    }
}
