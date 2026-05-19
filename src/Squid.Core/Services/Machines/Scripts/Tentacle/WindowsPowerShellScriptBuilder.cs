namespace Squid.Core.Services.Machines.Scripts.Tentacle;

/// <summary>
/// One-liner PowerShell installer + register script for Windows Tentacle agents.
/// Mirrors <see cref="LinuxBinaryScriptBuilder"/>'s shape and step ordering so an
/// operator copying-and-pasting on Windows gets the same UX as on Linux.
///
/// <para><b>Step ordering</b> (matches Linux): install → discover → register →
/// service install. <c>install-tentacle.ps1</c> is invoked with <c>-NoServiceInstall</c>
/// so register can write config BEFORE the SCM starts the worker process.
/// Otherwise the service would start with no persisted config → crash within
/// seconds → operator sees a confusing "service stopped immediately" instead
/// of a clean register failure.</para>
///
/// <para><b>Path discovery via install-info.json</b>: <c>install-tentacle.ps1</c>
/// writes <c>%ProgramData%\Squid\Tentacle\install-info.json</c> after extraction.
/// Step 2 of this generated script reads that file to locate the binary, so
/// operators who override <c>$env:INSTALL_DIR</c> (or pass <c>-InstallDir</c>)
/// don't break Steps 3-4. No hardcoded paths — the discovery file is the
/// source of truth.</para>
///
/// <para><b>UAC auto-elevation</b>: <c>install-tentacle.ps1</c> auto-elevates
/// via UAC when not run as Administrator (both file and <c>irm | iex</c>
/// modes). The generated script does not need to know about elevation — it
/// just invokes the installer, which handles the re-launch.</para>
///
/// <para><b>403 detection</b>: the register call wraps <c>$LASTEXITCODE</c>
/// detection so operators hitting <c>MachineCreate</c> permission denials
/// get a structured error message naming the missing permission AND the
/// built-in roles that grant it (Environment Manager, Space Owner, System
/// Administrator). Without this hint, the raw 403 is opaque.</para>
///
/// <para><b>Why backtick continuation</b>: PowerShell uses backtick (<c>`</c>)
/// for line continuation. A backslash at end-of-line would be parsed as a
/// path-separator typo and the register call would fail with cryptic
/// argv-parse errors.</para>
///
/// <para><b>Flavor selection</b>: emits <c>--flavor LinuxTentacle</c>
/// matching what <c>install-tentacle.sh</c> uses today. The shipped
/// <c>Squid.Tentacle.exe</c> binary supports the LinuxTentacle flavor's
/// registration mechanics on Windows; a dedicated WindowsTentacleFlavor is
/// a future concern.</para>
/// </summary>
public sealed class WindowsPowerShellScriptBuilder : TentacleInstallScriptBuilderBase
{
    public override string Id => "windows-powershell";
    public override string Label => "PowerShell + Windows Service";
    public override string OperatingSystem => "Windows";
    public override string InstallationMethod => "PowerShell";
    public override string ScriptType => "powershell";
    public override bool IsRecommended => true;

    /// <summary>
    /// Discovery-file path written by <c>install-tentacle.ps1</c>. The generated
    /// script reads this to locate the binary regardless of install dir.
    /// Pinned by a unit test — renaming requires lockstep update in install-tentacle.ps1.
    /// </summary>
    internal const string DiscoveryFileRelativePath = @"Squid\Tentacle\install-info.json";

    /// <summary>
    /// Canonical binary filename. The .NET publish output is <c>Squid.Tentacle.exe</c>
    /// (per <c>Squid.Tentacle.csproj</c> RootNamespace + workflow publish step).
    /// NOT <c>squid-tentacle.exe</c> (that's the Linux shell wrapper).
    /// </summary>
    internal const string BinaryFileName = "Squid.Tentacle.exe";

    protected override string BuildContent(TentacleInstallContext context)
    {
        var command = context.Command;

        var lines = new List<string>
        {
            "# Step 1: Download + extract Tentacle binary (auto-elevates via UAC if needed)",
            "Invoke-WebRequest -UseBasicParsing -Uri https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.ps1 -OutFile $env:TEMP\\install-tentacle.ps1",
            "& $env:TEMP\\install-tentacle.ps1 -NoServiceInstall",
            "",
            "# Step 2: Locate the Tentacle binary (smart discovery)",
            "# Works for BOTH workflows:",
            "#   (a) Paste mode  - Step 1 above wrote install-info.json - this branch picks it up.",
            "#   (b) Download mode - operator manually downloaded + extracted the zip; install-info.json",
            "#       absent, so we fall back to common install locations and PATH lookup.",
            $"$infoPath = Join-Path $env:ProgramData '{DiscoveryFileRelativePath}'",
            "$tentacle = $null",
            "",
            "# Try 1: install-info.json (canonical, used by Paste mode + custom -InstallDir)",
            "if (Test-Path $infoPath) {",
            "    try {",
            "        $candidate = (Get-Content -Raw $infoPath | ConvertFrom-Json).BinaryPath",
            "        if ($candidate -and (Test-Path $candidate)) { $tentacle = $candidate }",
            "    } catch { Write-Host \"Could not parse install-info.json - falling back to default locations.\" }",
            "}",
            "",
            "# Try 2: default install dir under %ProgramFiles% (Download-mode default)",
            "if (-not $tentacle) {",
            $"    $defaultPath = Join-Path $env:ProgramFiles 'Squid Tentacle\\{BinaryFileName}'",
            "    if (Test-Path $defaultPath) { $tentacle = $defaultPath }",
            "}",
            "",
            "# Try 3: PATH lookup (operator added install dir to PATH)",
            "if (-not $tentacle) {",
            $"    $onPath = Get-Command '{BinaryFileName}' -ErrorAction SilentlyContinue",
            "    if ($onPath) { $tentacle = $onPath.Source }",
            "}",
            "",
            "if (-not $tentacle) {",
            "    throw @\"",
            $"Could not locate {BinaryFileName}. Tried (in order):",
            $"  1. install-info.json at $infoPath",
            $"  2. default install path: $env:ProgramFiles\\Squid Tentacle\\{BinaryFileName}",
            $"  3. PATH lookup for {BinaryFileName}",
            "",
            "Either run Step 1 above (which writes install-info.json), OR manually download + extract the",
            "Tentacle zip to '$env:ProgramFiles\\Squid Tentacle\\', OR set $tentacle yourself before this line.",
            "\"@",
            "}",
            "Write-Host \"Found Tentacle binary at: $tentacle\"",
            "",
            "# Step 3: Register agent with server"
        };

        var registerArgs = BuildRegisterArgs(context, command);
        lines.Add(JoinLinesPowerShell(registerArgs.ToArray()));

        lines.Add("");
        lines.Add(BuildRegisterExitCodeHandler());

        lines.Add("");
        lines.Add("# Step 4: Install as Windows Service");
        lines.Add("& $tentacle service install --instance Default --service-name squid-tentacle");
        lines.Add("if ($LASTEXITCODE -ne 0) {");
        lines.Add("    throw \"Service install failed (exit $LASTEXITCODE). Review the output above for SCM errors.\"");
        lines.Add("}");

        return string.Join("\n", lines);
    }

    private static List<string> BuildRegisterArgs(TentacleInstallContext context, Squid.Message.Commands.Machine.GenerateTentacleInstallScriptCommand command)
    {
        var args = new List<string>
        {
            "& $tentacle register",
            $"--server \"{command.ServerUrl}\"",
            $"--api-key \"{context.ApiKey}\"",
            "--flavor LinuxTentacle"
        };

        if (!string.IsNullOrWhiteSpace(context.RolesCsv))
            args.Add($"--role \"{context.RolesCsv}\"");

        if (!string.IsNullOrWhiteSpace(context.EnvironmentsCsv))
            args.Add($"--environment \"{context.EnvironmentsCsv}\"");

        if (!string.IsNullOrWhiteSpace(command.MachineName))
            args.Add($"--name \"{command.MachineName}\"");

        if (context.IsListening)
        {
            args.Add($"--listening-host \"{command.ListeningHostName}\"");
            args.Add($"--listening-port \"{command.ListeningPort}\"");
        }
        else
        {
            args.Add($"--comms-url \"{command.ServerCommsUrl}\"");
        }

        if (!string.IsNullOrWhiteSpace(context.ServerThumbprint))
            args.Add($"--server-cert \"{context.ServerThumbprint}\"");

        return args;
    }

    /// <summary>
    /// Emits an exit-code handler that turns a register-call <c>403</c> into a
    /// structured error message. Operators hitting <c>MachineCreate</c> permission
    /// denials get a clear remediation path instead of opaque "HTTP 403".
    ///
    /// <para>Built-in roles with <c>MachineCreate</c> (per <c>BuiltInRoles</c>
    /// in <c>BuiltInRoleSeeder.cs</c>): Environment Manager, Space Owner.
    /// System Administrator does NOT grant MachineCreate — it's a system-level
    /// role for managing spaces/users/teams, not a space-resource role.
    /// Pinned by <c>PermissionRoleResolverTests.GetBuiltInRolesGranting_MachineCreate_...</c>.</para>
    /// </summary>
    private static string BuildRegisterExitCodeHandler()
    {
        return @"if ($LASTEXITCODE -ne 0) {
    if ($LASTEXITCODE -eq 403) {
        throw @""
Register failed with HTTP 403 -- the API key user lacks the 'MachineCreate' permission.

Resolve via the Squid Web UI:
  1. Assign 'Environment Manager' role to the API key's owner user, OR
  2. Assign 'Space Owner' role for full space access, OR
  3. Add 'MachineCreate' permission to the owner's current role, OR
  4. Issue a new API key from a user with one of those roles.

Built-in roles that grant MachineCreate:
  - Environment Manager
  - Space Owner
""@
    }
    throw ""Register failed (exit $LASTEXITCODE). Review the output above.""
}";
    }

    private static string JoinLinesPowerShell(params string[] lines) => string.Join(" `\n", lines);
}
