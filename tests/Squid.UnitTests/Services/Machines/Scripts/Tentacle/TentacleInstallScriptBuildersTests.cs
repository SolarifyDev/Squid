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

        // register
        script.Content.ShouldContain("squid-tentacle.exe' register");
        script.Content.ShouldContain("--listening-host \"host.local\"");
        script.Content.ShouldContain("--listening-port \"10933\"");
        script.Content.ShouldContain("--server-cert \"FAF04764\"");

        // service install must come AFTER register
        var registerIdx = script.Content.IndexOf("register", StringComparison.Ordinal);
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
    public void WindowsPowerShell_BinaryPath_PinsCanonicalProgramFilesLocation()
    {
        // install-tentacle.ps1 default install dir is C:\Program Files\Squid Tentacle.
        // Pinning the literal here ensures the script and the installer agree —
        // a future installer that defaults elsewhere would silently break this
        // generated script (binary not found at the path the script invokes).
        var script = new WindowsPowerShellScriptBuilder().Build(ListeningContext());

        script.Content.ShouldContain(@"C:\Program Files\Squid Tentacle\squid-tentacle.exe", customMessage:
            "Generated script must reference the canonical install path written by install-tentacle.ps1. " +
            "If install-tentacle.ps1's default ever changes, update both sides — caught by this pin.");
    }

    [Fact]
    public void WindowsPowerShell_ContainsAllThreeOrderedSteps()
    {
        // Defensive ordering pin: the script must contain Step 1 (install),
        // Step 2 (register), Step 3 (service install) in that order. A refactor
        // that accidentally reorders them would break the registration flow
        // because service install before register starts the SCM-managed
        // process with no config → it crashes within seconds → operator sees
        // a confusing "service stopped immediately" rather than a clean error.
        var script = new WindowsPowerShellScriptBuilder().Build(ListeningContext());

        var step1Idx = script.Content.IndexOf("Step 1", StringComparison.Ordinal);
        var step2Idx = script.Content.IndexOf("Step 2", StringComparison.Ordinal);
        var step3Idx = script.Content.IndexOf("Step 3", StringComparison.Ordinal);

        step1Idx.ShouldBeGreaterThan(-1);
        step2Idx.ShouldBeGreaterThan(step1Idx);
        step3Idx.ShouldBeGreaterThan(step2Idx);
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
