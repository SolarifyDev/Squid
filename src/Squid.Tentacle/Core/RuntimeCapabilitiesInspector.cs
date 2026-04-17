using System.Diagnostics;
using Serilog;

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

        return metadata;
    }

    private static string DetectOs()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsMacOS()) return "macOS";
        if (OperatingSystem.IsLinux()) return "Linux";
        return "Unknown";
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
