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
        unit.ShouldContain("Restart=always");
        unit.ShouldContain("WantedBy=multi-user.target");
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
