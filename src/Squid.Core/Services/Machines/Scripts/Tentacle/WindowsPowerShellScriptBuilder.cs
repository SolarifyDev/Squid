namespace Squid.Core.Services.Machines.Scripts.Tentacle;

/// <summary>
/// One-liner PowerShell installer + register script for Windows Tentacle agents.
/// Mirrors <see cref="LinuxBinaryScriptBuilder"/>'s shape and step ordering so an
/// operator copying-and-pasting on Windows gets the same UX as on Linux.
///
/// <para><b>Step ordering</b> (matches Linux): install → register → service install.
/// Installing the Windows Service BEFORE register would have the SCM start the
/// process with no persisted config → service crashes within seconds → operator
/// sees a confusing "service stopped immediately" instead of a clean register
/// failure. So <c>install-tentacle.ps1</c> is invoked with <c>-NoServiceInstall</c>
/// to defer Step 3 until after Step 2 has written the config.</para>
///
/// <para><b>Why backtick continuation</b>: PowerShell uses backtick (<c>`</c>)
/// for line continuation. A backslash at end-of-line (the bash convention used
/// by <see cref="TentacleInstallScriptBuilderBase.JoinLines"/>) would be parsed
/// as a path-separator typo and the register call would fail with cryptic
/// argv-parse errors. The local <see cref="JoinLinesPowerShell"/> helper
/// emits the correct continuation token; pinned by a unit test.</para>
///
/// <para><b>Flavor selection</b>: emits <c>--flavor LinuxTentacle</c> matching
/// what <c>install-tentacle.sh</c> uses today. The shipped <c>squid-tentacle.exe</c>
/// binary supports the LinuxTentacle flavor's registration mechanics on Windows;
/// a dedicated WindowsTentacleFlavor is a future concern (would distinguish
/// Windows-specific runtime behaviour, not the registration RPC which is
/// already OS-agnostic on the server side).</para>
/// </summary>
public sealed class WindowsPowerShellScriptBuilder : TentacleInstallScriptBuilderBase
{
    public override string Id => "windows-powershell";
    public override string Label => "PowerShell + Windows Service";
    public override string OperatingSystem => "Windows";
    public override string InstallationMethod => "PowerShell";
    public override string ScriptType => "powershell";
    public override bool IsRecommended => true;

    private const string CanonicalBinaryPath = @"C:\Program Files\Squid Tentacle\squid-tentacle.exe";

    protected override string BuildContent(TentacleInstallContext context)
    {
        var command = context.Command;

        var lines = new List<string>
        {
            "# Step 1: Download + extract Tentacle binary",
            "Invoke-WebRequest -UseBasicParsing -Uri https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.ps1 -OutFile $env:TEMP\\install-tentacle.ps1",
            "& $env:TEMP\\install-tentacle.ps1 -NoServiceInstall",
            "",
            "# Step 2: Register agent with server"
        };

        // The register CLI runs as the current (elevated) user. Windows has no
        // sudo equivalent — operator runs the whole script in an Administrator
        // PowerShell session. Config writes land under %ProgramData%\Squid\Tentacle\
        // where the LocalSystem service account can read it (same per-user-vs-
        // service-account concern the Linux builder solves with sudo, just
        // resolved by a different OS mechanism).
        var registerArgs = new List<string>
        {
            $"& '{CanonicalBinaryPath}' register",
            $"--server \"{command.ServerUrl}\"",
            $"--api-key \"{context.ApiKey}\"",
            "--flavor LinuxTentacle"
        };

        if (!string.IsNullOrWhiteSpace(context.RolesCsv))
            registerArgs.Add($"--role \"{context.RolesCsv}\"");

        if (!string.IsNullOrWhiteSpace(context.EnvironmentsCsv))
            registerArgs.Add($"--environment \"{context.EnvironmentsCsv}\"");

        if (!string.IsNullOrWhiteSpace(command.MachineName))
            registerArgs.Add($"--name \"{command.MachineName}\"");

        if (context.IsListening)
        {
            registerArgs.Add($"--listening-host \"{command.ListeningHostName}\"");
            registerArgs.Add($"--listening-port \"{command.ListeningPort}\"");
        }
        else
        {
            registerArgs.Add($"--comms-url \"{command.ServerCommsUrl}\"");
        }

        // Same TLS-thumbprint pinning rationale as Linux builders: the Tentacle's
        // initial register-with HTTP call (Listening) and every Halibut poll
        // handshake (Polling) verify this against the Server's actual cert.
        // Without it, ServerCertificateValidator falls back to
        // "accept with warning" — works but unsafe.
        if (!string.IsNullOrWhiteSpace(context.ServerThumbprint))
            registerArgs.Add($"--server-cert \"{context.ServerThumbprint}\"");

        lines.Add(JoinLinesPowerShell(registerArgs.ToArray()));
        lines.Add("");
        lines.Add("# Step 3: Install as Windows Service");
        lines.Add($"& '{CanonicalBinaryPath}' service install");

        return string.Join("\n", lines);
    }

    private static string JoinLinesPowerShell(params string[] lines) => string.Join(" `\n", lines);
}
