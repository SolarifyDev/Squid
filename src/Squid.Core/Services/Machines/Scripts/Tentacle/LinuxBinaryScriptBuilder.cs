namespace Squid.Core.Services.Machines.Scripts.Tentacle;

public sealed class LinuxBinaryScriptBuilder : TentacleInstallScriptBuilderBase
{
    public override string Id => "linux-binary";
    public override string Label => "Binary + systemd";
    public override string OperatingSystem => "Linux";
    public override string InstallationMethod => "Binary";
    public override string ScriptType => "bash";

    protected override string BuildContent(TentacleInstallContext ctx)
    {
        var command = ctx.Command;

        var lines = new List<string>
        {
            "# Step 1: Install Tentacle",
            "curl -fsSL https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.sh | sudo bash",
            "",
            "# Step 2: Register with server"
        };

        var registerArgs = new List<string>
        {
            "squid-tentacle register",
            $"--server \"{command.ServerUrl}\"",
            $"--api-key \"{ctx.ApiKey}\"",
            "--flavor LinuxTentacle"
        };

        if (!string.IsNullOrWhiteSpace(ctx.RolesCsv))
            registerArgs.Add($"--role \"{ctx.RolesCsv}\"");

        if (!string.IsNullOrWhiteSpace(ctx.EnvironmentsCsv))
            registerArgs.Add($"--environment \"{ctx.EnvironmentsCsv}\"");

        if (!string.IsNullOrWhiteSpace(command.MachineName))
            registerArgs.Add($"--name \"{command.MachineName}\"");

        if (ctx.IsListening)
        {
            registerArgs.Add($"--listening-host \"{command.ListeningHostName}\"");
            registerArgs.Add($"--listening-port \"{command.ListeningPort}\"");
        }
        else
        {
            registerArgs.Add($"--comms-url \"{command.ServerCommsUrl}\"");
        }

        // Server TLS thumbprint pinning applies to BOTH modes. Listening: the Tentacle's
        // initial HTTP register-with call verifies the Server's cert. Polling: every
        // Halibut poll connection verifies it on every handshake. Without --server-cert
        // ServerCertificateValidator falls into backward-compat "accept with warning"
        // — works but unsafe.
        if (!string.IsNullOrWhiteSpace(ctx.ServerThumbprint))
            registerArgs.Add($"--server-cert \"{ctx.ServerThumbprint}\"");

        lines.Add(JoinLines(registerArgs.ToArray()));
        lines.Add("");
        lines.Add("# Step 3: Install as systemd service");
        lines.Add("sudo squid-tentacle service install");

        return string.Join("\n", lines);
    }
}
