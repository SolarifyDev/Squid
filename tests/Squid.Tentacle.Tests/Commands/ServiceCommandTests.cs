using Squid.Tentacle.Commands;

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
        unit.ShouldContain("Restart=always");
        unit.ShouldContain("RestartSec=10");
        unit.ShouldContain("KillSignal=SIGINT");
        unit.ShouldContain("TimeoutStopSec=60");

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
}
