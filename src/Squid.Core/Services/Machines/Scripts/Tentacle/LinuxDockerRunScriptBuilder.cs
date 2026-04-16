namespace Squid.Core.Services.Machines.Scripts.Tentacle;

public sealed class LinuxDockerRunScriptBuilder : TentacleInstallScriptBuilderBase
{
    private const string DefaultImage = "squidcd/squid-tentacle-linux:latest";

    public override string Id => "linux-docker-run";
    public override string Label => "Docker Run";
    public override string OperatingSystem => "Linux";
    public override string InstallationMethod => "Docker";
    public override string ScriptType => "docker-cli";
    public override bool IsRecommended => true;

    protected override string BuildContent(TentacleInstallContext ctx)
    {
        var command = ctx.Command;
        var image = string.IsNullOrWhiteSpace(command.DockerImage) ? DefaultImage : command.DockerImage;

        var lines = new List<string>
        {
            "docker run -d --name squid-tentacle",
            $"-e Tentacle__ServerUrl=\"{command.ServerUrl}\"",
            $"-e Tentacle__ApiKey=\"{ctx.ApiKey}\"",
            $"-e Tentacle__Roles=\"{ctx.RolesCsv}\"",
            $"-e Tentacle__Environments=\"{ctx.EnvironmentsCsv}\"",
            $"-e Tentacle__SpaceId=\"{command.SpaceId}\""
        };

        if (ctx.IsListening)
        {
            lines.Add($"-e Tentacle__ListeningHostName=\"{command.ListeningHostName}\"");
            lines.Add($"-e Tentacle__ListeningPort=\"{command.ListeningPort}\"");
            lines.Add($"-p {command.ListeningPort}:{command.ListeningPort}");
        }
        else
        {
            lines.Add($"-e Tentacle__ServerCommsUrl=\"{command.ServerCommsUrl}\"");
        }

        // Server TLS thumbprint pinning — required for both modes. Without this
        // ServerCertificateValidator falls into backward-compat "accept with warning"
        // which works but is insecure in production. We always emit when the server
        // has a self-signed cert (i.e. always, in the current Squid design).
        if (!string.IsNullOrWhiteSpace(ctx.ServerThumbprint))
            lines.Add($"-e Tentacle__ServerCertificate=\"{ctx.ServerThumbprint}\"");

        if (!string.IsNullOrWhiteSpace(command.MachineName))
            lines.Add($"-e Tentacle__MachineName=\"{command.MachineName}\"");

        lines.Add("-v squid-tentacle-certs:/opt/squid/certs");
        lines.Add(image);

        return JoinLines(lines.ToArray());
    }
}
