using System.Diagnostics;
using System.Runtime.Versioning;

namespace Squid.Tentacle.ServiceHost;

/// <summary>
/// P1-Phase12.C — Windows Services backend driven by <c>sc.exe</c>. Mirrors the
/// shape of <see cref="SystemdServiceHost"/> (pure-function argv builders for
/// testability, single shell-out helper for invocation, exit-code passthrough).
/// </summary>
/// <remarks>
/// <para><b>Why <c>sc.exe</c> and not <c>System.ServiceProcess.ServiceController</c></b>:
/// <c>ServiceController</c> requires the <c>System.ServiceProcess.ServiceController</c>
/// NuGet (Windows-only) and only exposes Start/Stop/Status — service install
/// still has to shell to <c>sc.exe</c>. Going through one tool for every
/// operation keeps the dependency graph simple, matches the well-documented
/// Octopus Tentacle pattern, and means operators see the SAME error messages
/// they'd see invoking <c>sc.exe</c> directly.</para>
///
/// <para><b>Default service identity</b>: <c>obj= LocalSystem</c> is what
/// <c>sc create</c> uses without an explicit <c>obj=</c> argument; specifying
/// it explicitly here makes the contract visible in the audit trail. Custom
/// users (non-empty <c>RunAsUser</c>) require an LSA <c>SeServiceLogonRight</c>
/// grant + a <c>password=</c> arg — Phase-12.C scope is LocalSystem only;
/// Phase-12.C-followup will add the LSA-grant + WMI <c>Win32_Service.Change</c>
/// dance for operator <c>--username</c>/<c>--password</c> input.</para>
///
/// <para><b>Already-installed handling</b>: <c>sc create</c> returns 1073
/// ("specified service already exists") if the service is registered. We
/// surface this as a clear error message rather than silently overwriting —
/// the operator should explicitly <c>service uninstall</c> first. (Linux
/// systemd overwrites the unit file silently; Windows is more conservative
/// because deleting a service mid-flight while it has handles open can leave
/// it "marked for deletion" until reboot.)</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceHost : IServiceHost
{
    /// <summary>Default identity when <see cref="ServiceInstallRequest.RunAsUser"/>
    /// is empty. Pinned per Rule 8 — air-gapped operators may rely on the
    /// literal in audit logs / SCM dumps.</summary>
    public const string DefaultServiceUser = "LocalSystem";

    /// <summary>The Windows service-control tool. Always present at
    /// <c>%SystemRoot%\System32\sc.exe</c>; using bare <c>"sc.exe"</c> resolves
    /// via PATH.</summary>
    public const string ScExeFileName = "sc.exe";

    public string DisplayName => "Windows Service Manager";

    public bool IsSupported => OperatingSystem.IsWindows();

    public int Install(ServiceInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ServiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecStart);

        // Validate the binary exists before registering a service that the SCM
        // would repeatedly fail to start with cryptic 1053 ("did not respond
        // in a timely fashion") or 193 (not a valid Win32 application) errors.
        if (!File.Exists(request.ExecStart))
        {
            Console.Error.WriteLine($"Error: binary not found at {request.ExecStart}");
            Console.Error.WriteLine("  Was the Tentacle installed? Check the install path or rerun the installer.");
            return 1;
        }

        var createArgs = BuildScCreateArgs(request);
        var createExit = Sc(createArgs);

        if (createExit != 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Service '{request.ServiceName}' creation failed (sc.exe exit {createExit}).");

            // 1073 = "specified service already exists" — most common operator slip.
            if (createExit == 1073)
                Console.Error.WriteLine("  Service already exists. Run 'squid-tentacle service uninstall' first.");

            return createExit;
        }

        Console.WriteLine($"Created Windows service '{request.ServiceName}'.");

        // sc create doesn't auto-start; mirror systemd's "create + enable + start"
        // by running sc start so 'service install' is one-shot.
        var startExit = Sc(BuildScStartArgs(request.ServiceName));

        if (startExit != 0)
        {
            Console.Error.WriteLine($"Service '{request.ServiceName}' created but failed to start (sc.exe exit {startExit}).");
            Console.Error.WriteLine($"  Check Event Viewer → Windows Logs → System for SCM errors,");
            Console.Error.WriteLine($"  or run 'squid-tentacle service status' for the SCM state.");
            return startExit;
        }

        Console.WriteLine($"Service '{request.ServiceName}' installed and started.");
        Console.WriteLine($"  Status:  sc query {request.ServiceName}");
        Console.WriteLine($"  Stop:    sc stop {request.ServiceName}");
        return 0;
    }

    public int Uninstall(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        // Best-effort stop — ignore exit code (service may already be stopped,
        // or may not exist). 'sc delete' will report the real verdict.
        Sc(BuildScStopArgs(serviceName));

        var deleteExit = Sc(BuildScDeleteArgs(serviceName));

        if (deleteExit == 0)
        {
            Console.WriteLine($"Service '{serviceName}' uninstalled.");
            return 0;
        }

        Console.Error.WriteLine($"Service '{serviceName}' uninstall failed (sc.exe exit {deleteExit}).");

        // 1060 = "specified service does not exist" — already gone, treat as success.
        if (deleteExit == 1060)
        {
            Console.WriteLine($"  (Service '{serviceName}' was not registered; nothing to remove.)");
            return 0;
        }

        return deleteExit;
    }

    public int Start(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        return Sc(BuildScStartArgs(serviceName));
    }

    public int Stop(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        return Sc(BuildScStopArgs(serviceName));
    }

    public int Status(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        return Sc(BuildScQueryArgs(serviceName));
    }

    // ── Pure-function argv builders (cross-platform testable) ──────────────────

    /// <summary>
    /// Builds the argv for <c>sc.exe create</c>. Pure function — every output
    /// is deterministic from the request; pinned by tests so any drift in the
    /// SCM contract (e.g., dropped <c>start= auto</c>) is compile/test-time
    /// visible.
    /// </summary>
    /// <remarks>
    /// <para><b>sc.exe argv quirk</b>: each option is a single argv element of
    /// the form <c>"key= value"</c> with a MANDATORY space after <c>=</c>.
    /// Dropping the space ("key=value") makes sc.exe parse it as a positional
    /// argument and the create silently fails with a confusing usage error.</para>
    ///
    /// <para><b>binPath wrapping</b>: the exe path is wrapped in escaped
    /// double quotes (<c>"\"…\""</c>) so paths with spaces (<c>C:\Program Files\…</c>)
    /// survive sc.exe's value parser. Args are appended unquoted — they're
    /// already split by space when the SCM launches the process.</para>
    /// </remarks>
    internal static string[] BuildScCreateArgs(ServiceInstallRequest request)
    {
        var execArgsTail = request.ExecArgs is { Length: > 0 }
            ? " " + string.Join(" ", request.ExecArgs)
            : string.Empty;

        var binPathValue = $"\"{request.ExecStart}\"{execArgsTail}";

        var displayName = string.IsNullOrWhiteSpace(request.Description)
            ? request.ServiceName
            : request.Description;

        var runAsUser = string.IsNullOrEmpty(request.RunAsUser)
            ? DefaultServiceUser
            : request.RunAsUser;

        return new[]
        {
            "create",
            request.ServiceName,
            $"binPath= {binPathValue}",
            $"DisplayName= {displayName}",
            "start= auto",
            $"obj= {runAsUser}"
        };
    }

    internal static string[] BuildScDeleteArgs(string serviceName)
        => new[] { "delete", serviceName };

    internal static string[] BuildScStartArgs(string serviceName)
        => new[] { "start", serviceName };

    internal static string[] BuildScStopArgs(string serviceName)
        => new[] { "stop", serviceName };

    internal static string[] BuildScQueryArgs(string serviceName)
        => new[] { "query", serviceName };

    // ── Shell-out helper ───────────────────────────────────────────────────────

    /// <summary>
    /// Invoke <c>sc.exe</c> with the given argv, stream stdout/stderr to the
    /// caller's console, and return the exit code. Mirrors
    /// <see cref="SystemdServiceHost"/>'s <c>Systemctl</c> helper.
    /// </summary>
    private static int Sc(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(ScExeFileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);

            if (process == null)
            {
                Console.Error.WriteLine($"Failed to start {ScExeFileName} (Process.Start returned null).");
                return 1;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(30));

            if (!string.IsNullOrWhiteSpace(stdout)) Console.Write(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.Write(stderr);

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to run sc.exe {string.Join(' ', args)}: {ex.Message}");
            return 1;
        }
    }
}
