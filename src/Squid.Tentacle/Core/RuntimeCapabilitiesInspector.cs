using System.Diagnostics;
using Serilog;
using Squid.Message.Constants;

namespace Squid.Tentacle.Core;

/// <summary>
/// Probes the host at capability-service build time to discover OS kind and
/// which shells are installed. Results are surfaced to the server through the
/// Halibut <c>CapabilitiesResponse.Metadata</c> dictionary so
/// <c>TentacleEndpointVariableContributor</c> can project them as deployment
/// variables (<c>Squid.Tentacle.OS</c>, <c>Squid.Tentacle.DefaultShell</c>,
/// <c>Squid.Tentacle.InstalledShells</c>). The server uses these to choose
/// script syntax and bootstrap wrapping.
/// </summary>
public static class RuntimeCapabilitiesInspector
{
    public const string MetaOs = "os";
    public const string MetaOsVersion = "osVersion";
    public const string MetaDefaultShell = "defaultShell";
    public const string MetaInstalledShells = "installedShells";
    public const string MetaArchitecture = "architecture";

    /// <summary>
    /// H7 — comma-separated detected system roles (e.g. <c>"iis,docker"</c>).
    /// Server-side <see cref="Squid.Core.Services.DeploymentExecution.Validation.MachineCapabilitySet"/>
    /// reads this and projects into per-role <c>role:{name}</c> slots so
    /// handlers can declare role requirements that the
    /// <c>CapabilityValidator</c> catches at plan-time.
    /// </summary>
    public const string MetaInstalledRoles = "installedRoles";

    public static Dictionary<string, string> Inspect()
    {
        var metadata = new Dictionary<string, string>
        {
            [MetaOs] = DetectOs(),
            [MetaOsVersion] = Environment.OSVersion.VersionString,
            [MetaArchitecture] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()
        };

        var shells = DetectInstalledShells();
        metadata[MetaInstalledShells] = string.Join(",", shells);
        metadata[MetaDefaultShell] = PickDefaultShell(shells);

        var roles = DetectInstalledRoles();
        metadata[MetaInstalledRoles] = string.Join(",", roles);

        return metadata;
    }

    /// <summary>
    /// H7 — detect installed system roles. Per-role probes are cheap
    /// (single service / binary check, ~50ms each) so the whole pass adds
    /// well under 1s to the Capabilities RPC. Failure to probe a role is
    /// silent — operator just doesn't see that role advertised, and
    /// handler requirements that depend on it fall through to
    /// optimistic-allow (same as a pre-H7 agent).
    /// </summary>
    private static List<string> DetectInstalledRoles()
    {
        var roles = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            // IIS — W3SVC is the canonical IIS web-server service. Existence
            // (any state — running/stopped) is enough to know IIS is installed;
            // a stopped service is still installed and the deploy script can
            // start it. Probe: `sc.exe query W3SVC` exit code 0 = service
            // exists. Avoids loading the WebAdministration module which
            // requires .NET Framework 3.5.1 on older Server SKUs.
            if (ServiceExistsOnWindows("W3SVC")) roles.Add("iis");

            // Docker Desktop / Docker EE — `docker.exe` on PATH AND service
            // exists. Avoid `docker info` which can take 5s+ on misconfigured
            // hosts.
            if (IsExecutableOnPath("docker") && ServiceExistsOnWindows("com.docker.service"))
                roles.Add("docker");
        }
        else if (OperatingSystem.IsLinux())
        {
            // systemd is the standard Linux init system on every supported
            // distribution Squid targets. Probe via the binary existence
            // (cheaper than `systemctl --version`).
            if (IsExecutableOnPath("systemctl")) roles.Add("systemd");

            // Docker daemon — `docker` binary + the systemd service. The
            // service may be active or inactive; either way the daemon is
            // installable. Skip the `docker info` round-trip.
            if (IsExecutableOnPath("docker") && IsSystemdUnitInstalled("docker"))
                roles.Add("docker");

            // nginx — checked via the systemd unit. Same install-vs-active
            // distinction as docker.
            if (IsSystemdUnitInstalled("nginx")) roles.Add("nginx");
        }

        return roles;
    }

    /// <summary>
    /// Probe whether a Windows service exists (any state). Exit code 0 from
    /// <c>sc.exe query &lt;name&gt;</c> means the service is registered with
    /// SCM. 1060 (ERROR_SERVICE_DOES_NOT_EXIST) means not installed. Other
    /// exit codes (permission etc.) → conservative "not installed" to avoid
    /// false-positive advertisement.
    /// </summary>
    private static bool ServiceExistsOnWindows(string serviceName)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"query {serviceName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(2_000);
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to probe Windows service {Service}", serviceName);
            return false;
        }
    }

    /// <summary>
    /// Probe whether a systemd unit is installed (any state). Exit code 0 OR
    /// 3 (ACTIVE / INACTIVE — installed but not running) means "installed";
    /// exit code 4 (NotFound) means "not installed". Other exit codes
    /// → conservative "not installed".
    /// </summary>
    private static bool IsSystemdUnitInstalled(string unitName)
    {
        if (!OperatingSystem.IsLinux()) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"-c 'systemctl status {unitName}.service > /dev/null 2>&1; echo $?'",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(2_000);
            var output = proc.StandardOutput.ReadToEnd().Trim();
            // systemctl status: 0 = active, 3 = inactive/dead, 4 = not-found
            // Treat 0 and 3 as "installed", 4 as "not installed".
            return output == "0" || output == "3";
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to probe systemd unit {Unit}", unitName);
            return false;
        }
    }

    private static string DetectOs()
    {
        // these strings are the agent half of the
        // cross-process OS-aware routing contract. The server side
        // (MachineRuntimeCapabilities.IsWindows / IsLinux / IsMacOS /
        // IsUnknown) reads them via Capabilities RPC. Use the centralized
        // AgentOperatingSystems constants so a rename here surfaces as a
        // build-time symbol-not-found on every consumer (server's strategy
        // resolvers, version registry, scoped variable contributor) —
        // rather than a runtime "no strategy registered" silent breakage.
        if (OperatingSystem.IsWindows()) return AgentOperatingSystems.Windows;
        if (OperatingSystem.IsMacOS()) return AgentOperatingSystems.MacOS;
        if (OperatingSystem.IsLinux()) return AgentOperatingSystems.Linux;
        return AgentOperatingSystems.Unknown;
    }

    private static List<string> DetectInstalledShells()
    {
        var shells = new List<string>();

        if (IsExecutableOnPath("pwsh")) shells.Add("pwsh");

        if (OperatingSystem.IsWindows())
        {
            if (IsExecutableOnPath("powershell")) shells.Add("powershell");
            if (IsExecutableOnPath("cmd")) shells.Add("cmd");
        }
        else
        {
            if (IsExecutableOnPath("bash")) shells.Add("bash");
            if (IsExecutableOnPath("sh")) shells.Add("sh");
        }

        return shells;
    }

    private static string PickDefaultShell(IReadOnlyList<string> shells)
    {
        if (shells.Contains("pwsh")) return "pwsh";
        if (OperatingSystem.IsWindows() && shells.Contains("powershell")) return "powershell";
        if (shells.Contains("bash")) return "bash";
        return shells.Count > 0 ? shells[0] : "unknown";
    }

    private static bool IsExecutableOnPath(string name)
    {
        try
        {
            var args = OperatingSystem.IsWindows() ? $"/c where {name}" : $"-c 'command -v {name}'";
            var fileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(2_000);
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to probe for executable {Name}", name);
            return false;
        }
    }
}
