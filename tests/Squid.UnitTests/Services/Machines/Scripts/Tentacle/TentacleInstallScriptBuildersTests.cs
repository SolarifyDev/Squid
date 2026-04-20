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
