using System.Diagnostics;
using Serilog;

namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.A.3 — Linux impl. Wraps the pre-Phase-12 logic from
/// <c>ServiceCommand.DetectServiceUser</c> + <c>InstanceOwnershipHandover</c>.
/// </summary>
public sealed class LinuxServiceUserProvider : IServiceUserProvider
{
    /// <summary>Pinned literal — install-tentacle.sh creates this exact user.</summary>
    public string DefaultServiceUser => "squid-tentacle";

    public bool IsRunningElevated()
    {
        // euid == 0 is the canonical "root" check. Environment.UserName == "root"
        // is what pre-Phase-12 code did; preserve it bit-for-bit.
        if (!OperatingSystem.IsLinux()) return false;

        return Environment.UserName.Equals("root", StringComparison.Ordinal);
    }

    public bool ServiceUserExists(string user)
    {
        if (string.IsNullOrEmpty(user)) return false;

        try
        {
            var psi = new ProcessStartInfo("getent", $"passwd {user}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            proc.WaitForExit(TimeSpan.FromSeconds(5));
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public bool TrySetOwnership(string path, string user)
    {
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(path)) return false;

        try
        {
            var psi = new ProcessStartInfo("chown", $"-R {user}:{user} \"{path}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            proc.WaitForExit(TimeSpan.FromSeconds(10));
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ServiceUserProvider] chown -R {User} {Path} failed", user, path);
            return false;
        }
    }
}
