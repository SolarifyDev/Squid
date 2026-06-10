using Serilog;
using Serilog.Events;
using Squid.Tentacle.Platform;

namespace Squid.Tentacle.Core;

/// <summary>
/// Builds the tentacle's Serilog logger. The console sink is ALWAYS present
/// (stderr-routed — keeps stdout clean for CLI pipelines). A persistent,
/// size-rotated FILE sink is added ONLY when the process runs as the
/// long-lived managed service (Windows SCM): there the console is attached to
/// NUL, so without a file sink the agent's entire runtime log is lost.
///
/// <para>Short-lived CLI commands (<c>version</c>, <c>show-thumbprint</c>,
/// <c>register</c>, …) stay console-only so they don't litter a log file per
/// invocation. On Linux/systemd the console is captured by journald, so the
/// file sink isn't needed there — the gap this closes is specifically the
/// Windows service, whose console output goes nowhere.</para>
/// </summary>
public static class TentacleLogging
{
    /// <summary>Per-file size cap before the rolling sink starts a new file.</summary>
    public const long FileSizeLimitBytes = 50L * 1024 * 1024;   // 50 MB

    /// <summary>Rolled files (including the active one) retained before the oldest is deleted.</summary>
    public const int RetainedFileCountLimit = 5;

    /// <summary>Subfolder (under the system config dir) the agent log lives in.</summary>
    public const string LogsDirName = "logs";

    /// <summary>Active agent log filename; Serilog appends a roll suffix to older files.</summary>
    public const string AgentLogFileName = "tentacle.log";

    /// <summary>Size cap for the SCM-startup diagnostic log — a separate append-only
    /// writer that predates the host + rolling sink, so the rolling sink can't cap it.</summary>
    public const long ScmDiagnosticMaxBytes = 1024 * 1024;   // 1 MB

    /// <summary>
    /// Best-effort single-generation rotation for a plain append-only log file:
    /// if it exceeds <paramref name="maxBytes"/>, move it to <c>{path}.old</c>
    /// (overwriting any prior <c>.old</c>) and start fresh. Never throws — a
    /// diagnostic writer must never disrupt the path it instruments.
    /// </summary>
    public static void RotateIfOversized(string path, long maxBytes)
    {
        try
        {
            var info = new FileInfo(path);

            if (!info.Exists || info.Length < maxBytes) return;

            File.Copy(path, path + ".old", overwrite: true);
            File.WriteAllText(path, string.Empty);
        }
        catch
        {
            // Best-effort — rotation must never throw.
        }
    }

    /// <summary>
    /// The persistent agent-log path: <c>{system-config-dir}/logs/tentacle.log</c>
    /// (e.g. <c>%ProgramData%\Squid\Tentacle\logs\tentacle.log</c> on Windows).
    /// </summary>
    public static string ResolveAgentLogPath()
        => Path.Combine(PlatformPaths.GetSystemConfigDir(), LogsDirName, AgentLogFileName);

    /// <summary>
    /// Builds the logger. <paramref name="addPersistentFileSink"/> is true only
    /// when running as the managed service. <paramref name="logPathOverride"/> is
    /// a test seam — production passes null and the path resolves to
    /// <see cref="ResolveAgentLogPath"/>.
    /// </summary>
    public static LoggerConfiguration BuildLoggerConfiguration(bool addPersistentFileSink, string logPathOverride = null, long? fileSizeLimitBytesOverride = null)
    {
        var config = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                standardErrorFromLevel: LogEventLevel.Verbose,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

        if (addPersistentFileSink && TryResolveWritableLogPath(logPathOverride, out var logPath))
            config.WriteTo.File(
                logPath,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: fileSizeLimitBytesOverride ?? FileSizeLimitBytes,
                retainedFileCountLimit: RetainedFileCountLimit,
                shared: true,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}");

        return config;
    }

    /// <summary>
    /// Resolves the log path and ensures its directory exists. Best-effort: if
    /// the directory can't be created (permission denied, read-only FS), returns
    /// false so the caller stays console-only — a log file must NEVER block the
    /// service from starting.
    /// </summary>
    private static bool TryResolveWritableLogPath(string logPathOverride, out string logPath)
    {
        logPath = null;

        try
        {
            var resolved = string.IsNullOrWhiteSpace(logPathOverride) ? ResolveAgentLogPath() : logPathOverride;
            var dir = Path.GetDirectoryName(resolved);

            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            logPath = resolved;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
