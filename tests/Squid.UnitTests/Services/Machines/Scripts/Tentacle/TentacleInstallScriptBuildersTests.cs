using System.Linq;
using Squid.Core.Services.Machines.Scripts.Tentacle;
using Squid.Message.Commands.Machine;

namespace Squid.UnitTests.Services.Machines.Scripts.Tentacle;

public class TentacleInstallScriptBuildersTests
{
    // ========================================================================
    // LinuxDockerRunScriptBuilder
    // ========================================================================

    [Fact]
    public void DockerRun_Listening_ContainsPortMappingAndListeningHost()
    {
        var builder = new LinuxDockerRunScriptBuilder();
        var script = builder.Build(ListeningContext());

        script.Id.ShouldBe("linux-docker-run");
        script.OperatingSystem.ShouldBe("Linux");
        script.InstallationMethod.ShouldBe("Docker");
        script.ScriptType.ShouldBe("docker-cli");
        script.IsRecommended.ShouldBeTrue();

        script.Content.ShouldContain("docker run -d --name squid-tentacle");
        script.Content.ShouldContain("Tentacle__ListeningHostName=\"host.local\"");
        script.Content.ShouldContain("Tentacle__ListeningPort=\"10933\"");
        script.Content.ShouldContain("-p 10933:10933");
        script.Content.ShouldNotContain("Tentacle__ServerCommsUrl");
    }

    [Fact]
    public void DockerRun_Polling_UsesServerCommsUrl_NoPortMapping()
    {
        var builder = new LinuxDockerRunScriptBuilder();
        var script = builder.Build(PollingContext());

        script.Content.ShouldContain("Tentacle__ServerCommsUrl=\"https://squid:10943\"");
        script.Content.ShouldNotContain("ListeningHostName");
        script.Content.ShouldNotContain("-p 10933");
    }

    [Fact]
    public void DockerRun_CustomImage_UsesProvidedImage()
    {
        var ctx = ListeningContext(cmd => cmd.DockerImage = "custom/tentacle:1.2.3");
        var script = new LinuxDockerRunScriptBuilder().Build(ctx);

        script.Content.ShouldContain("custom/tentacle:1.2.3");
        script.Content.ShouldNotContain("squidcd/squid-tentacle-linux:latest");
    }

    [Fact]
    public void DockerRun_EmitsServerCertificateEnv_BothModes()
    {
        // Regression guard: TLS thumbprint pinning env var must be present for both
        // Polling and Listening. Earlier versions of this builder omitted it entirely.
        new LinuxDockerRunScriptBuilder().Build(ListeningContext(serverThumbprint: "FAF04764"))
            .Content.ShouldContain("Tentacle__ServerCertificate=\"FAF04764\"");

        new LinuxDockerRunScriptBuilder().Build(PollingContext())
            .Content.ShouldContain("Tentacle__ServerCertificate=\"FAF04764\"");
    }

    [Fact]
    public void DockerRun_NoServerCertificate_OmitsEnvVar()
    {
        // When the server can't supply a thumbprint (shouldn't normally happen in
        // production) we just skip the env var rather than emit an empty quoted value
        // that would confuse Kestrel/Halibut during registration.
        var ctx = ListeningContext(serverThumbprint: "");
        var script = new LinuxDockerRunScriptBuilder().Build(ctx);

        script.Content.ShouldNotContain("Tentacle__ServerCertificate");
    }

    // ========================================================================
    // LinuxDockerComposeScriptBuilder
    // ========================================================================

    [Fact]
    public void DockerCompose_Listening_IncludesPortsAndListeningHost()
    {
        var script = new LinuxDockerComposeScriptBuilder().Build(ListeningContext());

        script.Id.ShouldBe("linux-docker-compose");
        script.ScriptType.ShouldBe("compose-yaml");
        script.IsRecommended.ShouldBeFalse();

        script.Content.ShouldContain("services:");
        script.Content.ShouldContain("  squid-tentacle:");
        script.Content.ShouldContain("Tentacle__ListeningHostName: \"host.local\"");
        script.Content.ShouldContain("    ports:");
        script.Content.ShouldContain("- \"10933:10933\"");
    }

    [Fact]
    public void DockerCompose_Polling_UsesServerCommsUrl_NoPortsSection()
    {
        var script = new LinuxDockerComposeScriptBuilder().Build(PollingContext());

        script.Content.ShouldContain("Tentacle__ServerCommsUrl: \"https://squid:10943\"");
        script.Content.ShouldNotContain("    ports:");
        script.Content.ShouldNotContain("ListeningHostName");
    }

    [Fact]
    public void DockerCompose_EmitsServerCertificateEnv_BothModes()
    {
        new LinuxDockerComposeScriptBuilder().Build(ListeningContext(serverThumbprint: "FAF04764"))
            .Content.ShouldContain("Tentacle__ServerCertificate: \"FAF04764\"");

        new LinuxDockerComposeScriptBuilder().Build(PollingContext())
            .Content.ShouldContain("Tentacle__ServerCertificate: \"FAF04764\"");
    }

    // ========================================================================
    // LinuxBinaryScriptBuilder
    // ========================================================================

    [Fact]
    public void Binary_Listening_IncludesServerCertAndListeningArgs()
    {
        var ctx = ListeningContext(serverThumbprint: "FAF04764");
        var script = new LinuxBinaryScriptBuilder().Build(ctx);

        script.Id.ShouldBe("linux-binary");
        script.ScriptType.ShouldBe("bash");

        script.Content.ShouldContain("curl -fsSL");
        script.Content.ShouldContain("squid-tentacle register");
        script.Content.ShouldContain("--listening-host \"host.local\"");
        script.Content.ShouldContain("--listening-port \"10933\"");
        script.Content.ShouldContain("--server-cert \"FAF04764\"");
        script.Content.ShouldContain("squid-tentacle service install");
    }

    [Fact]
    public void Binary_RegisterStep_UsesSudo()
    {
        // Regression: without `sudo`, register runs as the invoking user → writes
        // config to ~/.config/squid-tentacle/... Then `sudo service install` runs
        // the service as `squid-tentacle` user, which looks for config under
        // /etc/squid-tentacle/... → missing → falls back to appsettings.json
        // defaults → UnauthorizedAccessException at startup. `sudo register`
        // persists to /etc/squid-tentacle/... where the service user can find it.
        var script = new LinuxBinaryScriptBuilder().Build(ListeningContext());

        script.Content.ShouldContain("sudo squid-tentacle register");
        // Defense in depth: ensure we didn't just add sudo while still emitting
        // the bare form on a separate line.
        script.Content.ShouldNotContain("\nsquid-tentacle register");
    }

    [Fact]
    public void Binary_Polling_IncludesCommsUrlAndServerCert()
    {
        // --server-cert is required for BOTH Polling and Listening now: Polling tentacles
        // need to pin the Server's Halibut cert on every poll handshake. Earlier versions
        // only emitted --server-cert in the Listening branch, which left Polling deployments
        // silently relying on backward-compat accept-with-warning behaviour.
        var script = new LinuxBinaryScriptBuilder().Build(PollingContext());

        script.Content.ShouldContain("--comms-url \"https://squid:10943\"");
        script.Content.ShouldContain("--server-cert \"FAF04764\"");
        script.Content.ShouldNotContain("--listening-host");
    }

    [Fact]
    public void Binary_WithMachineNameAndRoles_IncludesThemInRegisterArgs()
    {
        var ctx = ListeningContext(cmd =>
        {
            cmd.MachineName = "web-01";
            cmd.Tags = ["web", "frontend"];
            cmd.Environments = ["Production", "Staging"];
        });

        var script = new LinuxBinaryScriptBuilder().Build(ctx);

        script.Content.ShouldContain("--name \"web-01\"");
        script.Content.ShouldContain("--role \"web,frontend\"");
        script.Content.ShouldContain("--environment \"Production,Staging\"");
    }

    // ========================================================================
    // Metadata contract — all Linux builders agree on OS + expose unique Id
    // ========================================================================

    [Fact]
    public void AllLinuxBuilders_ShareLinuxOs_AndHaveUniqueIds()
    {
        var builders = new ITentacleInstallScriptBuilder[]
        {
            new LinuxDockerRunScriptBuilder(),
            new LinuxDockerComposeScriptBuilder(),
            new LinuxBinaryScriptBuilder()
        };

        builders.ShouldAllBe(b => b.OperatingSystem == "Linux");
        builders.Select(b => b.Id).Distinct().Count().ShouldBe(builders.Length);
        builders.Count(b => b.IsRecommended).ShouldBe(1,
            "Exactly one builder per OS should be marked as recommended (default selection)");
    }

    // ========================================================================
    // WindowsPowerShellScriptBuilder
    //
    // Mirrors LinuxBinaryScriptBuilder's contract for Windows: download the
    // canonical installer.ps1, run it with -NoServiceInstall (binary + firewall
    // only, NO sc.exe yet), then register, then `service install`. Same step
    // ordering as the Linux script — register BEFORE service install so the
    // SCM start finds a valid config and doesn't crash on PermissionDenied
    // mid-Phase-A.
    // ========================================================================

    [Fact]
    public void WindowsPowerShell_Listening_IncludesServerCertAndListeningArgs()
    {
        var ctx = ListeningContext(serverThumbprint: "FAF04764");
        var script = new WindowsPowerShellScriptBuilder().Build(ctx);

        script.Id.ShouldBe("windows-powershell");
        script.OperatingSystem.ShouldBe("Windows");
        script.InstallationMethod.ShouldBe("PowerShell");
        script.ScriptType.ShouldBe("powershell");
        script.IsRecommended.ShouldBeTrue(
            "Only one Windows builder today — must be the default selection.");

        // install-tentacle.ps1 download + invoke
        script.Content.ShouldContain("Invoke-WebRequest");
        script.Content.ShouldContain("install-tentacle.ps1");
        script.Content.ShouldContain("-NoServiceInstall", customMessage:
            "Step 1 must defer service install so register can write config first " +
            "(matches LinuxBinaryScriptBuilder's install-then-register-then-service order).");

        // register — uses the $tentacle variable discovered from install-info.json
        // (no hardcoded path; survives operator's -InstallDir override)
        script.Content.ShouldContain("$tentacle register");
        script.Content.ShouldContain("--listening-host \"host.local\"");
        script.Content.ShouldContain("--listening-port \"10933\"");
        script.Content.ShouldContain("--server-cert \"FAF04764\"");

        // service install must come AFTER register
        var registerIdx = script.Content.IndexOf("$tentacle register", StringComparison.Ordinal);
        var serviceInstallIdx = script.Content.IndexOf("service install", StringComparison.Ordinal);
        registerIdx.ShouldBeGreaterThan(0);
        serviceInstallIdx.ShouldBeGreaterThan(registerIdx,
            "service install MUST run AFTER register so the Windows Service starts with a valid config.");
    }

    [Fact]
    public void WindowsPowerShell_Polling_IncludesCommsUrlAndServerCert()
    {
        // --server-cert is required for both modes — same rationale as Linux:
        // Polling tentacles pin the Server's Halibut cert on every poll handshake.
        var script = new WindowsPowerShellScriptBuilder().Build(PollingContext());

        script.Content.ShouldContain("--comms-url \"https://squid:10943\"");
        script.Content.ShouldContain("--server-cert \"FAF04764\"");
        script.Content.ShouldNotContain("--listening-host");
        script.Content.ShouldNotContain("--listening-port");
    }

    [Fact]
    public void WindowsPowerShell_PowerShellLineContinuation_NotBashBackslash()
    {
        // PowerShell uses backtick (`) for line continuation. A backslash at
        // EOL would be parsed as a path-separator-typo and the register call
        // would fail with cryptic argv-parse errors. Pin the literal char so
        // a future refactor that reuses LinuxBinaryScriptBuilder's bash join
        // helper (which emits `\\\n`) can't slip past review.
        var script = new WindowsPowerShellScriptBuilder().Build(ListeningContext());

        script.Content.ShouldContain(" `\n", customMessage:
            "Multi-line `register` invocation must use PowerShell backtick continuation.");
        script.Content.ShouldNotContain(" \\\n", customMessage:
            "Bash backslash-newline continuation is invalid PowerShell — would cause " +
            "the register call to fail with confusing 'is not recognized' errors.");
    }

    [Fact]
    public void WindowsPowerShell_NoSudo()
    {
        // Windows has no `sudo`. The script assumes elevated PowerShell already
        // (the operator runs `Start-Process powershell -Verb RunAs` or right-click
        // → Run as Administrator). A literal `sudo` would error with
        // "'sudo' is not recognized" and abort the install.
        var script = new WindowsPowerShellScriptBuilder().Build(ListeningContext());

        script.Content.ShouldNotContain("sudo");
    }

    [Fact]
    public void WindowsPowerShell_WithMachineNameAndRoles_IncludesThemInRegisterArgs()
    {
        var ctx = ListeningContext(cmd =>
        {
            cmd.MachineName = "win-web-01";
            cmd.Tags = ["web", "frontend"];
            cmd.Environments = ["Production", "Staging"];
        });

        var script = new WindowsPowerShellScriptBuilder().Build(ctx);

        script.Content.ShouldContain("--name \"win-web-01\"");
        script.Content.ShouldContain("--role \"web,frontend\"");
        script.Content.ShouldContain("--environment \"Production,Staging\"");
    }

    [Fact]
    public void WindowsPowerShell_NoServerCertificate_OmitsArg()
    {
        // Same shape as Linux builders — when server can't supply a thumbprint,
        // omit the arg rather than emitting --server-cert "" which the register
        // CLI would reject as malformed.
        var ctx = ListeningContext(serverThumbprint: "");
        var script = new WindowsPowerShellScriptBuilder().Build(ctx);

        script.Content.ShouldNotContain("--server-cert");
    }

    [Fact]
    public void WindowsPowerShell_DoesNotHardcodeBinaryPath_ReadsInstallInfoJson()
    {
        // Path-agnostic invariant: the generated script MUST NOT hardcode
        // C:\Program Files\Squid Tentacle\... because operators routinely
        // override -InstallDir for D: drive installs, dev-machine layouts,
        // or air-gapped per-tenant paths.
        //
        // Instead, the script reads %ProgramData%\Squid\Tentacle\install-info.json
        // (written by install-tentacle.ps1 post-extraction) and uses the
        // BinaryPath field. This survives any -InstallDir override.
        var script = new WindowsPowerShellScriptBuilder().Build(ListeningContext());

        script.Content.ShouldNotContain(@"C:\Program Files\Squid Tentacle\Squid.Tentacle.exe", customMessage:
            "Generated script hardcodes the default install path. Operators with custom -InstallDir " +
            "would silently break — read from install-info.json instead.");
        script.Content.ShouldNotContain(@"C:\Program Files\Squid Tentacle\squid-tentacle.exe", customMessage:
            "Generated script contains the deprecated lowercase 'squid-tentacle.exe' name. The actual " +
            "published binary is 'Squid.Tentacle.exe' (per Squid.Tentacle.csproj RootNamespace).");

        // Discovery-file path is pinned exactly — install-tentacle.ps1 writes here.
        script.Content.ShouldContain(@"Squid\Tentacle\install-info.json", customMessage:
            "Step 2 must read the discovery file at %ProgramData%\\Squid\\Tentacle\\install-info.json. " +
            "Renaming requires lockstep update in install-tentacle.ps1's Write-InstallInfo function.");

        // The `$tentacle` PowerShell variable is the resolved binary path used
        // throughout Steps 3-4. Pin both the assignment + the consumption.
        // (Smart discovery may set $tentacle from install-info.json's BinaryPath
        // field OR from one of the fallback paths -- the consumers stay
        // identical so the rest of the script is path-agnostic.)
        script.Content.ShouldContain(".BinaryPath",
            customMessage: "install-info.json's BinaryPath field must be consumed.");
        script.Content.ShouldContain("$tentacle register");
        script.Content.ShouldContain("$tentacle service install");
    }

    [Fact]
    public void WindowsPowerShell_GeneratedScript_ChecksRegisterExitCodeForHttp403WithPermissionHint()
    {
        // 403 from /api/machines/register/* almost always means the API key user
        // lacks the MachineCreate permission. Without a structured error message,
        // the operator just sees an exit code and has no idea what to do.
        //
        // The generated script wraps $LASTEXITCODE = 403 and emits the three
        // built-in roles that grant MachineCreate (Environment Manager / Space
        // Owner / System Administrator) so the operator can self-service the
        // fix.
        var script = new WindowsPowerShellScriptBuilder().Build(ListeningContext());

        script.Content.ShouldContain("$LASTEXITCODE -eq 403", customMessage:
            "Register exit-code handler must explicitly detect 403 — that's the canonical " +
            "permission-denied signal from /api/machines/register/*.");

        script.Content.ShouldContain("MachineCreate", customMessage:
            "403 error message must name the missing permission so operators know what to grant.");

        // The roles that actually grant MachineCreate must be named explicitly.
        // System Administrator is INTENTIONALLY omitted — it's a system-level
        // role and does not grant space-scoped MachineCreate (verified by
        // PermissionRoleResolverTests). Listing it would mislead operators.
        script.Content.ShouldContain("Environment Manager");
        script.Content.ShouldContain("Space Owner");
        script.Content.ShouldNotContain("System Administrator", customMessage:
            "System Administrator does NOT grant MachineCreate — suggesting it would mislead operators. " +
            "If BuiltInRoles.SystemAdministrator changes to include MachineCreate, update " +
            "WindowsPowerShellScriptBuilder.BuildRegisterExitCodeHandler AND this test.");
    }

    [Fact]
    public void WindowsPowerShell_ContainsAllFourOrderedSteps()
    {
        // Defensive ordering pin: the script must contain Step 1 (install),
        // Step 2 (discover), Step 3 (register), Step 4 (service install) in
        // that order. A refactor that accidentally reorders them breaks the
        // registration flow.
        //
        // Step 2 (discover) is new in the install-info.json design — it reads
        // the discovery file written by install-tentacle.ps1 and resolves the
        // $tentacle variable that Steps 3-4 use.
        var script = new WindowsPowerShellScriptBuilder().Build(ListeningContext());

        var step1Idx = script.Content.IndexOf("Step 1", StringComparison.Ordinal);
        var step2Idx = script.Content.IndexOf("Step 2", StringComparison.Ordinal);
        var step3Idx = script.Content.IndexOf("Step 3", StringComparison.Ordinal);
        var step4Idx = script.Content.IndexOf("Step 4", StringComparison.Ordinal);

        step1Idx.ShouldBeGreaterThan(-1);
        step2Idx.ShouldBeGreaterThan(step1Idx);
        step3Idx.ShouldBeGreaterThan(step2Idx);
        step4Idx.ShouldBeGreaterThan(step3Idx);
    }

    [Fact]
    public void WindowsPowerShell_Step2_SmartDiscovery_TriesInstallInfoJsonFirst()
    {
        // Paste-mode workflow: Step 1 (install-tentacle.ps1) writes install-info.json.
        // Step 2's first attempt is to read that file -- the canonical path.
        var script = new WindowsPowerShellScriptBuilder().Build(ListeningContext());

        script.Content.ShouldContain("Test-Path $infoPath", customMessage:
            "Step 2 must check install-info.json first (Paste-mode canonical path).");
        script.Content.ShouldContain(".BinaryPath", customMessage:
            "Discovery file's BinaryPath field is consumed when present.");
    }

    [Fact]
    public void WindowsPowerShell_Step2_SmartDiscovery_FallsBackToDefaultInstallDir()
    {
        // Download-mode workflow: operator manually downloads the zip + extracts to
        // the default location, NEVER ran install-tentacle.ps1, so install-info.json
        // doesn't exist. Step 2's fallback chain must still find the binary at
        // %ProgramFiles%\Squid Tentacle\Squid.Tentacle.exe.
        //
        // Without this fallback, Download-mode operators would hit a hard
        // "install-info.json not found" error and the snippet would be useless.
        var script = new WindowsPowerShellScriptBuilder().Build(ListeningContext());

        script.Content.ShouldContain("$env:ProgramFiles", customMessage:
            "Step 2 fallback must probe %ProgramFiles%\\Squid Tentacle for Download-mode operators.");
        script.Content.ShouldContain(@"Squid Tentacle\Squid.Tentacle.exe", customMessage:
            "Default install path probe must use the canonical 'Squid Tentacle\\Squid.Tentacle.exe' form.");
    }

    [Fact]
    public void WindowsPowerShell_Step2_SmartDiscovery_FallsBackToPathLookup()
    {
        // Last-resort fallback: operator added the install dir to PATH and didn't
        // use either standard location. Get-Command finds it via PATH.
        var script = new WindowsPowerShellScriptBuilder().Build(ListeningContext());

        script.Content.ShouldContain("Get-Command 'Squid.Tentacle.exe'", customMessage:
            "Step 2 must fall back to PATH lookup so operators with custom install + PATH entries " +
            "still work. Catches workflow where operator did everything by hand (Download + custom dir + PATH edit).");
    }

    [Fact]
    public void WindowsPowerShell_Step2_AllFallbacksExhausted_ActionableError()
    {
        // If install-info.json + default install dir + PATH lookup all miss,
        // the script must throw with an error that names every path tried AND
        // gives operator three explicit remediation paths.
        var script = new WindowsPowerShellScriptBuilder().Build(ListeningContext());

        script.Content.ShouldContain("Could not locate Squid.Tentacle.exe", customMessage:
            "All-fallbacks-exhausted error must name the binary so operators can grep for it.");
        script.Content.ShouldContain("install-info.json at $infoPath", customMessage:
            "Error must list the discovery file path it tried.");
        script.Content.ShouldContain("default install path", customMessage:
            "Error must list the default install path it tried.");
        script.Content.ShouldContain("PATH lookup", customMessage:
            "Error must list the PATH lookup it tried.");
        script.Content.ShouldContain("Either run Step 1 above", customMessage:
            "Error must give operator the three remediation paths (Paste mode / Download mode / manual $tentacle).");
    }

    [Fact]
    public void WindowsPowerShell_GeneratedContent_HasNoEmDashesInExecutableStrings()
    {
        // PowerShell 5.1 reads files using the host's OEM codepage when no BOM is
        // present. An em-dash (—, U+2014) in a string literal is decoded as
        // `â?"` under cp437/cp1252, which the PS parser then chokes on with
        // "Unexpected token" — entire script bails before reaching the first
        // executable line. CI failure 2026-05-19 (run 26077385538) hit exactly
        // this; the fix is to use ASCII `--` in strings.
        //
        // Comments are safe (parser skips them) so this check looks only at
        // string-literal context — any em-dash inside the generated script body
        // breaks operators on cp437/cp1252/cp936/cp932 hosts.
        var script = new WindowsPowerShellScriptBuilder().Build(ListeningContext());

        script.Content.ShouldNotContain("—", customMessage:
            "Generated PowerShell content contains an em-dash (—). PowerShell 5.1 on cp437/cp1252/cp936 " +
            "hosts mis-decodes this as 'â?\"' and fails to parse the surrounding statement. Use ASCII `--` instead. " +
            "Test pinned after CI failure on workflow run 26077385538.");
    }

    [Fact]
    public void WindowsPowerShell_PinsBinaryFileNameConstant_SquidTentacleExe()
    {
        // Drift detector for the canonical binary filename. The .NET publish
        // output is "Squid.Tentacle.exe" (Squid.Tentacle.csproj has
        // <RootNamespace>Squid.Tentacle</RootNamespace>, AssemblyName defaults
        // to project name). A rename in the csproj OR a future "lowercase
        // everything" refactor would silently break the generated script.
        WindowsPowerShellScriptBuilder.BinaryFileName.ShouldBe("Squid.Tentacle.exe", customMessage:
            "Renaming the published binary breaks every install-tentacle.ps1 invocation. " +
            "If you genuinely want to rename, update install-tentacle.ps1's $BinaryName " +
            "(line ~80) AND this pin in lockstep.");
    }

    [Fact]
    public void WindowsPowerShell_PinsDiscoveryFileRelativePath()
    {
        // Drift detector for the discovery-file location. install-tentacle.ps1
        // writes to %ProgramData%\Squid\Tentacle\install-info.json; the
        // generated script reads from the same path. Renaming one without the
        // other breaks every fresh install.
        WindowsPowerShellScriptBuilder.DiscoveryFileRelativePath.ShouldBe(@"Squid\Tentacle\install-info.json", customMessage:
            "Discovery-file path changed. install-tentacle.ps1's Write-InstallInfo writes here; " +
            "the generated script reads from the same constant. Update both sides in lockstep.");
    }

    // ========================================================================
    // Cross-OS metadata: Windows must NOT collide with Linux Ids and the
    // recommended-flag invariant must hold per OS.
    // ========================================================================

    [Fact]
    public void AllBuilders_HaveUniqueIds_AndOnePerOsIsRecommended()
    {
        var builders = new ITentacleInstallScriptBuilder[]
        {
            new LinuxDockerRunScriptBuilder(),
            new LinuxDockerComposeScriptBuilder(),
            new LinuxBinaryScriptBuilder(),
            new WindowsPowerShellScriptBuilder()
        };

        builders.Select(b => b.Id).Distinct().Count().ShouldBe(builders.Length,
            "Builder Ids must be globally unique — they're FE selection keys.");

        builders.Where(b => b.OperatingSystem == "Linux").Count(b => b.IsRecommended).ShouldBe(1);
        builders.Where(b => b.OperatingSystem == "Windows").Count(b => b.IsRecommended).ShouldBe(1);
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static TentacleInstallContext ListeningContext(Action<GenerateTentacleInstallScriptCommand> configure = null, string serverThumbprint = "FAF04764")
    {
        var cmd = new GenerateTentacleInstallScriptCommand
        {
            MachineName = "test-host",
            ServerUrl = "https://squid:7078",
            ServerCommsUrl = "https://squid:10943",
            Environments = ["Production"],
            Tags = ["web"],
            SpaceId = 1,
            CommunicationMode = "Listening",
            ListeningHostName = "host.local",
            ListeningPort = 10933
        };

        configure?.Invoke(cmd);

        return new TentacleInstallContext
        {
            Command = cmd,
            ApiKey = "API-TEST",
            ServerThumbprint = serverThumbprint,
            IsListening = true
        };
    }

    private static TentacleInstallContext PollingContext(Action<GenerateTentacleInstallScriptCommand> configure = null)
    {
        var cmd = new GenerateTentacleInstallScriptCommand
        {
            MachineName = "test-host",
            ServerUrl = "https://squid:7078",
            ServerCommsUrl = "https://squid:10943",
            Environments = ["Production"],
            Tags = ["web"],
            SpaceId = 1,
            CommunicationMode = "Polling"
        };

        configure?.Invoke(cmd);

        return new TentacleInstallContext
        {
            Command = cmd,
            ApiKey = "API-TEST",
            ServerThumbprint = "FAF04764",
            IsListening = false
        };
    }
}
