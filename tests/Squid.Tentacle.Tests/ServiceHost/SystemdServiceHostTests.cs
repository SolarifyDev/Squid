using Squid.Tentacle.ServiceHost;

namespace Squid.Tentacle.Tests.ServiceHost;

public class SystemdServiceHostTests
{
    [Fact]
    public void BuildUnitFile_ProducesExpectedUnit_ForSimpleRequest()
    {
        var unit = SystemdServiceHost.BuildUnitFile(new ServiceInstallRequest
        {
            ServiceName = "squid-tentacle",
            ExecStart = "/usr/local/bin/squid-tentacle",
            WorkingDirectory = "/opt/squid-tentacle"
        });

        unit.ShouldContain("[Unit]");
        unit.ShouldContain("Description=Squid Tentacle Agent (squid-tentacle)");
        unit.ShouldContain("ExecStart=/usr/local/bin/squid-tentacle");
        unit.ShouldContain("WorkingDirectory=/opt/squid-tentacle");
        unit.ShouldContain("Restart=on-failure",
            customMessage: "1.5.0 C4: Restart=always crash-loops a broken binary forever. " +
                           "`on-failure` + StartLimit gives up after 3 failed starts — operator sees " +
                           "`systemctl status` reporting 'failed' instead of endless restart spam.");
        unit.ShouldContain("WantedBy=multi-user.target");
    }

    [Fact]
    public void BuildUnitFile_HasStartLimitConfig_PreventsCrashLoopAfterBadUpgrade()
    {
        // 1.5.0 C4: if an upgrade installs a broken binary (glibc mismatch,
        // corrupt package, missing config), we want systemd to give up
        // after a bounded number of failed starts rather than crash-loop
        // at 10s intervals consuming CPU forever.
        //
        // Three literal pins so a refactor that "simplifies" the unit by
        // dropping any of them — re-opening the crash-loop regression —
        // fails here before hitting any agent.
        var unit = SystemdServiceHost.BuildUnitFile(new ServiceInstallRequest
        {
            ServiceName = "squid-tentacle",
            ExecStart = "/usr/local/bin/squid-tentacle",
            WorkingDirectory = "/opt/squid-tentacle"
        });

        unit.ShouldContain("StartLimitBurst=3",
            customMessage: "Bounded start-failure count; >3 would bloat recovery window, <3 would give up too fast on transient startup issues (network up but DNS not yet propagating, etc.).");
        unit.ShouldContain("StartLimitIntervalSec=120",
            customMessage: "Window over which StartLimitBurst is counted. 120s = 3 attempts × ~40s spacing (10s RestartSec + probe), comfortable for a real transient-failure-then-recovery.");
        unit.ShouldContain("StartLimitAction=none",
            customMessage: "After exceeding the burst, leave the unit in `failed` state (don't reboot the host). Operator intervention via `systemctl reset-failed` is the recovery path.");
    }

    [Fact]
    public void BuildUnitFile_WithExecArgs_AppendsThemToExecStart()
    {
        var unit = SystemdServiceHost.BuildUnitFile(new ServiceInstallRequest
        {
            ServiceName = "squid-tentacle-prod",
            ExecStart = "/usr/local/bin/squid-tentacle",
            WorkingDirectory = "/opt/squid-tentacle",
            ExecArgs = ["run", "--instance", "production"]
        });

        unit.ShouldContain("ExecStart=/usr/local/bin/squid-tentacle run --instance production");
    }

    [Fact]
    public void BuildUnitFile_WithRunAsUser_AddsUserAndGroupLines()
    {
        var unit = SystemdServiceHost.BuildUnitFile(new ServiceInstallRequest
        {
            ServiceName = "squid-tentacle",
            ExecStart = "/usr/local/bin/squid-tentacle",
            WorkingDirectory = "/opt/squid-tentacle",
            RunAsUser = "squid-tentacle"
        });

        unit.ShouldContain("User=squid-tentacle");
        unit.ShouldContain("Group=squid-tentacle");
    }

    [Fact]
    public void BuildUnitFile_CustomDescription_OverridesDefault()
    {
        var unit = SystemdServiceHost.BuildUnitFile(new ServiceInstallRequest
        {
            ServiceName = "x",
            ExecStart = "/y",
            WorkingDirectory = "/z",
            Description = "Custom desc"
        });

        unit.ShouldContain("Description=Custom desc");
        unit.ShouldNotContain("Description=Squid Tentacle Agent");
    }

    [Fact]
    public void BuildUnitFile_RegressionGuard_NeverEmitsDotnetDllForm()
    {
        // Early versions of ServiceCommand generated `ExecStart=dotnet Squid.Tentacle.dll run`
        // which never worked on target machines (no SDK, no .dll). This test guards against
        // regressing that fix.
        var unit = SystemdServiceHost.BuildUnitFile(new ServiceInstallRequest
        {
            ServiceName = "squid-tentacle",
            ExecStart = "/usr/local/bin/squid-tentacle",
            WorkingDirectory = "/opt/squid-tentacle"
        });

        unit.ShouldNotContain("dotnet Squid.Tentacle.dll");
    }

    [Fact]
    public void ServiceHostFactory_Resolve_ReturnsHostMatchingCurrentOs()
    {
        var host = ServiceHostFactory.Resolve();

        host.ShouldNotBeNull();
        host.IsSupported.ShouldBeTrue();
    }

    [Fact]
    public void ServiceHostFactory_Resolve_AlwaysReturnsExactlyOneSupportedHost()
    {
        // Among all candidates, exactly one IsSupported must fire on any given platform.
        // Protects against "two hosts both supported" (ambiguous) or "zero supported" (broken).
        var supportedCount = ServiceHostFactory.Candidates
            .Select(create => create())
            .Count(h => h.IsSupported);

        supportedCount.ShouldBe(1,
            $"Exactly one IServiceHost should be supported on the current OS; got {supportedCount}. " +
            "Check the IsSupported predicates on SystemdServiceHost / WindowsServiceHost / LaunchdServiceHost.");
    }

    [Fact]
    public void ServiceHostFactory_AllHosts_ReportDistinctDisplayNames()
    {
        var names = ServiceHostFactory.Candidates
            .Select(create => create().DisplayName)
            .ToList();

        names.Distinct().Count().ShouldBe(names.Count,
            "Every IServiceHost must have a unique DisplayName so diagnostics are unambiguous.");
    }
}
