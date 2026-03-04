using System.Diagnostics;

namespace Squid.Tentacle.Tests.Support.Environment;

public static class ExternalToolProbe
{
    public static bool HasHelm() => CanExecuteVersion("helm", "version --short");
    public static bool HasKubectl() => CanExecuteVersion("kubectl", "version --client=true");
    public static bool HasKind() => CanExecuteVersion("kind", "--version");

    static bool CanExecuteVersion(string fileName, string arguments)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
                return false;

            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // best effort
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
