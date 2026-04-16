namespace Squid.Core.Services.Machines.Scripts.Tentacle;

public sealed class LinuxDockerComposeScriptBuilder : TentacleInstallScriptBuilderBase
{
    private const string DefaultImage = "squidcd/squid-tentacle-linux:latest";

    public override string Id => "linux-docker-compose";
    public override string Label => "Docker Compose";
    public override string OperatingSystem => "Linux";
    public override string InstallationMethod => "DockerCompose";
    public override string ScriptType => "compose-yaml";

    protected override string BuildContent(TentacleInstallContext ctx)
    {
        var command = ctx.Command;
        var image = string.IsNullOrWhiteSpace(command.DockerImage) ? DefaultImage : command.DockerImage;

        var lines = new List<string>
        {
            "services:",
            "  squid-tentacle:",
            $"    image: {image}",
            "    environment:",
            $"      Tentacle__ServerUrl: \"{command.ServerUrl}\"",
            $"      Tentacle__ApiKey: \"{ctx.ApiKey}\"",
            $"      Tentacle__Roles: \"{ctx.RolesCsv}\"",
            $"      Tentacle__Environments: \"{ctx.EnvironmentsCsv}\"",
            $"      Tentacle__SpaceId: \"{command.SpaceId}\""
        };

        if (ctx.IsListening)
        {
            lines.Add($"      Tentacle__ListeningHostName: \"{command.ListeningHostName}\"");
            lines.Add($"      Tentacle__ListeningPort: \"{command.ListeningPort}\"");
            lines.Add("    ports:");
            lines.Add($"      - \"{command.ListeningPort}:{command.ListeningPort}\"");
        }
        else
        {
            lines.Add($"      Tentacle__ServerCommsUrl: \"{command.ServerCommsUrl}\"");
        }

        // TLS thumbprint pinning for both modes — see LinuxDockerRunScriptBuilder
        // for the rationale. Required for production correctness.
        if (!string.IsNullOrWhiteSpace(ctx.ServerThumbprint))
            lines.Add($"      Tentacle__ServerCertificate: \"{ctx.ServerThumbprint}\"");

        if (!string.IsNullOrWhiteSpace(command.MachineName))
            lines.Add($"      Tentacle__MachineName: \"{command.MachineName}\"");

        lines.Add("    volumes:");
        lines.Add("      - tentacle-certs:/opt/squid/certs");
        lines.Add("      - tentacle-work:/opt/squid/work");
        lines.Add("    restart: unless-stopped");
        lines.Add("");
        lines.Add("volumes:");
        lines.Add("  tentacle-certs:");
        lines.Add("  tentacle-work:");

        return string.Join("\n", lines);
    }
}
