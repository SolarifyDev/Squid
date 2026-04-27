using Shouldly;
using Squid.Tentacle.Commands;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Commands;

/// <summary>
/// P1-Phase9b.5 — pin the operator-facing self-diagnostic CLI contract.
///
/// <para><b>Why these tests matter</b>: <c>tentacle check-services</c> is
/// what an operator runs when an agent <em>looks</em> healthy in
/// <c>systemctl status</c> but server-side health checks fail. The output
/// must be parseable + actionable in EVERY failure scenario — a check that
/// silently returns "OK" when something's actually broken would defeat the
/// whole purpose. Tests pin both happy-path and each failure dimension.</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class CheckServicesCommandTests
{
    [Fact]
    public void Name_AndDescription_AreOperatorFacing()
    {
        var cmd = new CheckServicesCommand();

        cmd.Name.ShouldBe("check-services", customMessage:
            "Verb is operator-documented. Renaming breaks every runbook that references 'tentacle check-services'.");
        cmd.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void RunChecks_HappyPathSettings_ReturnsAllChecks()
    {
        // Settings populated with reasonable defaults — should produce all 5 checks.
        // Some checks may report failures depending on the local environment
        // (e.g. no agent cert exists yet on a fresh dev box) — what we PIN here
        // is that the check loop returns the expected number of checks and
        // that NONE of them throws an unhandled exception.
        var settings = new TentacleSettings
        {
            ServerUrl = "https://server.example.com",
            ServerCommsUrl = "https://server.example.com:10943"
        };

        var results = CheckServicesCommand.RunChecks(settings);

        results.Count.ShouldBe(5, customMessage:
            "Five checks: Configuration, Certificate, IScriptService, ICapabilitiesService, IFileTransferService.");

        var names = results.Select(r => r.Name).ToList();
        names.ShouldContain("Configuration");
        names.ShouldContain("Certificate");
        names.ShouldContain("IScriptService");
        names.ShouldContain("ICapabilitiesService");
        names.ShouldContain("IFileTransferService");
    }

    [Fact]
    public void RunChecks_NoServerUrl_FailsConfigurationCheck()
    {
        // Operator misconfigured the agent without a ServerUrl — must surface
        // explicitly, not silently report OK.
        var settings = new TentacleSettings { ServerUrl = "" };

        var results = CheckServicesCommand.RunChecks(settings);

        var configCheck = results.First(r => r.Name == "Configuration");
        configCheck.Pass.ShouldBeFalse(customMessage:
            "Empty ServerUrl must fail the Configuration check — operator action required.");
        configCheck.Detail.ShouldContain("ServerUrl",
            customMessage: "Failure detail must name the missing field for actionable diagnostic.");
    }

    [Fact]
    public void RunChecks_PollingMode_IsReportedInDetail()
    {
        var settings = new TentacleSettings
        {
            ServerUrl = "https://s.example.com",
            ServerCommsUrl = "https://s.example.com:10943"
        };

        var results = CheckServicesCommand.RunChecks(settings);

        var configCheck = results.First(r => r.Name == "Configuration");
        configCheck.Detail.ShouldContain("Polling");
    }

    [Fact]
    public void RunChecks_ListeningMode_IsReportedInDetail()
    {
        var settings = new TentacleSettings
        {
            ServerUrl = "https://s.example.com"
            // No ServerCommsUrl / ServerCommsAddresses → Listening
        };

        var results = CheckServicesCommand.RunChecks(settings);

        var configCheck = results.First(r => r.Name == "Configuration");
        configCheck.Detail.ShouldContain("Listening");
    }

    [Fact]
    public void RunChecks_CapabilitiesCheckBuildsResponseStructure()
    {
        // The capabilities check actually invokes CapabilitiesService internally.
        // Even with no on-disk upgrade-status file (typical fresh install),
        // the response must come back populated with SupportedServices.
        var settings = new TentacleSettings { ServerUrl = "https://s" };

        var results = CheckServicesCommand.RunChecks(settings);

        var capsCheck = results.First(r => r.Name == "ICapabilitiesService");
        capsCheck.Pass.ShouldBeTrue();
        capsCheck.Detail.ShouldContain("AgentVersion",
            customMessage: "Capabilities detail must include agent version for operator diagnostic.");
    }

    [Fact]
    public void RunChecks_FileTransferCheck_VerifiesUploadRootWritable()
    {
        // Default upload root is ~/.squid/uploads — LocalFileTransferService
        // creates it in its constructor. Test box should have HOME writable
        // → check passes.
        var settings = new TentacleSettings { ServerUrl = "https://s" };

        var results = CheckServicesCommand.RunChecks(settings);

        var ftCheck = results.First(r => r.Name == "IFileTransferService");
        ftCheck.Pass.ShouldBeTrue();
        ftCheck.Detail.ShouldContain("uploadRoot");
    }
}
