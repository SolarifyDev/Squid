using System.IO;
using Squid.Tentacle.Commands;
using Squid.Tentacle.Platform;

namespace Squid.Tentacle.Tests.Commands;

public class ServiceCommandTests
{
    // ========================================================================
    // GenerateUnitFile — verifies the exact systemd unit content
    // ========================================================================

    [Fact]
    public void GenerateUnitFile_ProducesExpectedUnitContent()
    {
        var unit = ServiceCommand.GenerateUnitFile(
            serviceName: "squid-tentacle",
            execStart: "/opt/squid-tentacle/Squid.Tentacle",
            workingDir: "/opt/squid-tentacle");

        unit.ShouldContain("[Unit]");
        unit.ShouldContain("Description=Squid Tentacle Agent (squid-tentacle)");
        unit.ShouldContain("After=network.target");

        unit.ShouldContain("[Service]");
        unit.ShouldContain("Type=simple");
        // ExecArgs are no longer hardcoded in GenerateUnitFile — ServiceCommand supplies them
        // (e.g. "run" or "run --instance NAME"). This test just verifies the ExecStart itself.
        unit.ShouldContain("ExecStart=/opt/squid-tentacle/Squid.Tentacle");
        unit.ShouldContain("WorkingDirectory=/opt/squid-tentacle");

        // 1.5.0 C4: Restart=on-failure (not `always`) + StartLimit* config
        // prevents a bad-upgrade crash-loop. Pinned here AND in
        // SystemdServiceHostTests.BuildUnitFile_HasStartLimitConfig_*.
        // Both tests are the contract — they both must update together.
        unit.ShouldContain("Restart=on-failure");
        unit.ShouldContain("RestartSec=10");
        unit.ShouldContain("StartLimitBurst=3");
        unit.ShouldContain("StartLimitIntervalSec=120");
        unit.ShouldContain("StartLimitAction=none");
        unit.ShouldContain("KillSignal=SIGINT");
        // : bumped 60 → 330 to cover the drain-default-300 + 30 grace.
        // Without this, systemd SIGKILLs Tentacle mid-drain, abruptly terminating
        // in-flight scripts. See SystemdServiceHost.BuildUnitFile XML doc.
        unit.ShouldContain("TimeoutStopSec=330");

        unit.ShouldContain("[Install]");
        unit.ShouldContain("WantedBy=multi-user.target");
    }

    [Fact]
    public void GenerateUnitFile_HonoursCustomServiceName()
    {
        var unit = ServiceCommand.GenerateUnitFile(
            serviceName: "squid-prod",
            execStart: "/opt/squid-tentacle/Squid.Tentacle",
            workingDir: "/opt/squid-tentacle");

        unit.ShouldContain("Description=Squid Tentacle Agent (squid-prod)");
    }

    [Fact]
    public void GenerateUnitFile_NeverEmitsDotnetDllForm()
    {
        // Regression: old code generated `ExecStart=dotnet Squid.Tentacle.dll run` for
        // PublishSingleFile binaries — which never worked on target machines (no SDK, no .dll).
        var unit = ServiceCommand.GenerateUnitFile(
            serviceName: "squid-tentacle",
            execStart: "/usr/local/bin/squid-tentacle",
            workingDir: "/opt/squid-tentacle");

        unit.ShouldNotContain("dotnet Squid.Tentacle.dll");
        unit.ShouldNotContain(" dll ");
    }

    // ========================================================================
    // ResolveServiceExecution — validates real-binary path resolution
    // ========================================================================

    [Fact]
    public void ResolveServiceExecution_ReturnsAbsoluteExecStartAndWorkingDir()
    {
        var (execStart, workingDir) = ServiceCommand.ResolveServiceExecution();

        // Must produce non-empty values
        execStart.ShouldNotBeNullOrWhiteSpace();
        workingDir.ShouldNotBeNullOrWhiteSpace();

        // Regression guard: must not fall back to the broken "dotnet *.dll" form
        execStart.ShouldNotStartWith("dotnet ");
        execStart.ShouldNotContain("Squid.Tentacle.dll");

        // Working dir must be a plausible absolute-ish path (no trailing slash)
        workingDir.ShouldNotEndWith("/");
        workingDir.ShouldNotEndWith("\\");
    }

    // ========================================================================
    // ResolveServiceExecution (testable overload) — flat vs versioned layout
    // ========================================================================

    private static string Root() => Path.Combine(Path.DirectorySeparatorChar.ToString(), "opt", "squid-tentacle");

    [Fact]
    public void ResolveServiceExecution_FlatInstall_ReturnsProcessPathVerbatim()
    {
        var root = Root();
        var processPath = Path.Combine(root, TentacleLayout.BinaryFileName);

        var (execStart, workingDir) = ServiceCommand.ResolveServiceExecution(root, processPath, _ => false);

        // Flat layout: register against the real exe exactly as today.
        execStart.ShouldBe(processPath);
        workingDir.ShouldBe(root);
    }

    [Fact]
    public void ResolveServiceExecution_VersionedRunningDir_LivePointer_ReturnsStablePointerPath()
    {
        var root = Root();
        var runningDir = TentacleLayout.VersionDir(root, "1.8.7");
        var processPath = TentacleLayout.VersionBinaryPath(root, "1.8.7");

        var (execStart, workingDir) = ServiceCommand.ResolveServiceExecution(runningDir, processPath, _ => true);

        // Versioned + live pointer: register against the STABLE pointer, not the
        // version-specific path, so upgrades repoint `current` without re-registering.
        execStart.ShouldBe(TentacleLayout.PointerBinaryPath(root));
        workingDir.ShouldBe(TentacleLayout.CurrentPointer(root));
    }

    [Fact]
    public void ResolveServiceExecution_CurrentPointerRunningDir_LivePointer_ReturnsStablePointerPath()
    {
        var root = Root();
        var runningDir = TentacleLayout.CurrentPointer(root);
        var processPath = TentacleLayout.PointerBinaryPath(root);

        var (execStart, workingDir) = ServiceCommand.ResolveServiceExecution(runningDir, processPath, _ => true);

        execStart.ShouldBe(TentacleLayout.PointerBinaryPath(root));
        workingDir.ShouldBe(TentacleLayout.CurrentPointer(root));
    }

    [Fact]
    public void ResolveServiceExecution_VersionedShapeButPointerNotLive_FallsBackToProcessPath()
    {
        // Safety: a path that LOOKS versioned but has no live `current` pointer (e.g.
        // mid-migration) must NOT be registered against a pointer that doesn't exist.
        var root = Root();
        var runningDir = TentacleLayout.VersionDir(root, "1.8.7");
        var processPath = TentacleLayout.VersionBinaryPath(root, "1.8.7");

        var (execStart, workingDir) = ServiceCommand.ResolveServiceExecution(runningDir, processPath, _ => false);

        execStart.ShouldBe(processPath);
        workingDir.ShouldBe(runningDir);
    }

    [Fact]
    public void ResolveServiceExecution_EmptyProcessPath_FlatFallback_UsesBinaryName()
    {
        var root = Root();

        var (execStart, workingDir) = ServiceCommand.ResolveServiceExecution(root, processPath: "", _ => false);

        execStart.ShouldBe(Path.Combine(root, TentacleLayout.BinaryFileName));
        workingDir.ShouldBe(root);
    }

    [Fact]
    public void ResolveServiceExecution_NullBaseDir_FallsBackToDefaultInstallDir()
    {
        var (execStart, workingDir) = ServiceCommand.ResolveServiceExecution(baseDir: null, processPath: "/usr/local/bin/Squid.Tentacle", _ => false);

        // Null base dir is never expected in practice (AppContext.BaseDirectory is always
        // set) but must degrade safely rather than throw.
        execStart.ShouldBe("/usr/local/bin/Squid.Tentacle");
        workingDir.ShouldBe("/opt/squid-tentacle");
    }
}
