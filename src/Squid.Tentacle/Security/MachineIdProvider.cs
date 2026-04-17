using System.Diagnostics;
using Serilog;

namespace Squid.Tentacle.Security;

/// <summary>
/// Reads a stable per-host identifier for key derivation. Order of preference:
///   - Linux: <c>/etc/machine-id</c> (systemd) → <c>/var/lib/dbus/machine-id</c>
///   - macOS: <c>ioreg</c> platform UUID
///   - Windows: HKLM\SOFTWARE\Microsoft\Cryptography MachineGuid
///   - Fallback: <c>gethostname()</c> + <c>Environment.MachineName</c> hash
///
/// The returned value is opaque — callers should not rely on format or compare
/// across machines. Presence is guaranteed (fallback never returns empty).
/// </summary>
public static class MachineIdProvider
{
    public static string Read()
    {
        var id = TryReadPlatformSpecific();
        if (!string.IsNullOrWhiteSpace(id)) return id.Trim();

        Log.Warning("Could not read platform machine id — falling back to hostname-based derivation. Secrets are still encrypted but a host rename will break decryption.");
        return $"fallback:{Environment.MachineName}:{Environment.UserDomainName}";
    }

    private static string TryReadPlatformSpecific()
    {
        if (OperatingSystem.IsLinux()) return TryReadLinux();
        if (OperatingSystem.IsMacOS()) return TryReadMacOs();
        if (OperatingSystem.IsWindows()) return TryReadWindows();
        return null;
    }

    private static string TryReadLinux()
    {
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            try { if (File.Exists(path)) return File.ReadAllText(path); }
            catch (IOException ex) { Log.Debug(ex, "Failed to read Linux machine-id at {Path}", path); }
        }
        return null;
    }

    private static string TryReadMacOs()
    {
        try
        {
            var psi = new ProcessStartInfo("ioreg", "-rd1 -c IOPlatformExpertDevice")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);

            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains("IOPlatformUUID")) continue;
                var parts = line.Split('=');
                if (parts.Length == 2) return parts[1].Trim().Trim('"');
            }
        }
        catch (Exception ex) { Log.Debug(ex, "Failed to read macOS IOPlatformUUID"); }

        return null;
    }

    private static string TryReadWindows()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var id = key?.GetValue("MachineGuid") as string;
            return id;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to read Windows MachineGuid");
            return null;
        }
    }
}
