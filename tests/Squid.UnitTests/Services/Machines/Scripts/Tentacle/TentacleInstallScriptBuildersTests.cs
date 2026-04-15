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
    public void Binary_Polling_IncludesCommsUrl_NoServerCert()
    {
        var script = new LinuxBinaryScriptBuilder().Build(PollingContext());

        script.Content.ShouldContain("--comms-url \"https://squid:10943\"");
        script.Content.ShouldNotContain("--listening-host");
        script.Content.ShouldNotContain("--server-cert");
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
