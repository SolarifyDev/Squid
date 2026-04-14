using System.Net;
using System.Security.Cryptography.X509Certificates;
using Squid.Message.Commands.Machine;
using Squid.Message.Constants;

namespace Squid.Core.Services.Machines;

public partial class MachineScriptService
{
    private const string DefaultTentacleImage = "squidcd/squid-tentacle-linux:latest";

    public async Task<GenerateTentacleInstallScriptResponse> GenerateTentacleInstallScriptAsync(
        GenerateTentacleInstallScriptCommand command, CancellationToken ct)
    {
        if (command == null)
            return Fail<GenerateTentacleInstallScriptResponse, GenerateTentacleInstallScriptData>(
                HttpStatusCode.BadRequest, "Command cannot be null");

        try
        {
            var apiKeyResult = await TryCreateTentacleApiKeyAsync(ct).ConfigureAwait(false);

            if (!apiKeyResult.Success)
                return Fail<GenerateTentacleInstallScriptResponse, GenerateTentacleInstallScriptData>(
                    apiKeyResult.Code, apiKeyResult.Message);

            var isListening = IsListeningMode(command.CommunicationMode);
            var serverThumbprint = GetServerThumbprint();

            var data = new GenerateTentacleInstallScriptData
            {
                DockerRunScript = BuildTentacleDockerRunScript(command, apiKeyResult.ApiKey, isListening),
                ScriptInstallScript = BuildTentacleScriptInstallScript(command, apiKeyResult.ApiKey, isListening, serverThumbprint),
                DockerComposeScript = BuildTentacleDockerComposeScript(command, apiKeyResult.ApiKey, isListening),
                ServerThumbprint = serverThumbprint
            };

            return Success<GenerateTentacleInstallScriptResponse, GenerateTentacleInstallScriptData>(data);
        }
        catch (Exception ex)
        {
            return Fail<GenerateTentacleInstallScriptResponse, GenerateTentacleInstallScriptData>(
                HttpStatusCode.InternalServerError, ex.Message);
        }
    }

    private async Task<(bool Success, string ApiKey, HttpStatusCode Code, string Message)> TryCreateTentacleApiKeyAsync(CancellationToken ct)
    {
        var description = $"Tentacle:{Guid.NewGuid():N}";
        var result = await _accountService.CreateApiKeyAsync(CurrentUsers.InternalUser.Id, description, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result?.ApiKey))
            return (false, null, HttpStatusCode.InternalServerError, "Failed to create API key");

        return (true, result.ApiKey, HttpStatusCode.OK, null);
    }

    private string GetServerThumbprint()
    {
        try
        {
            var certBytes = Convert.FromBase64String(_selfCertSetting.Base64);
            using var cert = X509CertificateLoader.LoadPkcs12(certBytes, _selfCertSetting.Password);

            return cert.Thumbprint;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsListeningMode(string communicationMode)
        => string.IsNullOrWhiteSpace(communicationMode)
        || communicationMode.Equals("Listening", StringComparison.OrdinalIgnoreCase);

    // ========================================================================
    // Docker Run
    // ========================================================================

    private static string BuildTentacleDockerRunScript(
        GenerateTentacleInstallScriptCommand command, string apiKey, bool isListening)
    {
        var image = string.IsNullOrWhiteSpace(command.DockerImage) ? DefaultTentacleImage : command.DockerImage;
        var roles = string.Join(",", command.Tags ?? []);
        var environments = string.Join(",", command.Environments ?? []);

        var lines = new List<string>
        {
            "docker run -d --name squid-tentacle",
            $"-e Tentacle__ServerUrl=\"{command.ServerUrl}\"",
            $"-e Tentacle__ApiKey=\"{apiKey}\"",
            $"-e Tentacle__Roles=\"{roles}\"",
            $"-e Tentacle__Environments=\"{environments}\"",
            $"-e Tentacle__SpaceId=\"{command.SpaceId}\""
        };

        if (isListening)
        {
            lines.Add($"-e Tentacle__ListeningHostName=\"{command.ListeningHostName}\"");
            lines.Add($"-e Tentacle__ListeningPort=\"{command.ListeningPort}\"");
            lines.Add($"-p {command.ListeningPort}:{command.ListeningPort}");
        }
        else
        {
            lines.Add($"-e Tentacle__ServerCommsUrl=\"{command.ServerCommsUrl}\"");
        }

        if (!string.IsNullOrWhiteSpace(command.MachineName))
            lines.Add($"-e Tentacle__MachineName=\"{command.MachineName}\"");

        lines.Add("-v squid-tentacle-certs:/opt/squid/certs");
        lines.Add(image);

        return JoinLines(lines.ToArray());
    }

    // ========================================================================
    // Script Install
    // ========================================================================

    private static string BuildTentacleScriptInstallScript(
        GenerateTentacleInstallScriptCommand command, string apiKey, bool isListening, string serverThumbprint)
    {
        var roles = string.Join(",", command.Tags ?? []);
        var environments = string.Join(",", command.Environments ?? []);

        var lines = new List<string>();

        // Step 1: Install
        lines.Add("# Step 1: Install Tentacle");
        lines.Add("curl -fsSL https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.sh | sudo bash");
        lines.Add("");

        // Step 2: Register
        lines.Add("# Step 2: Register with server");
        var registerArgs = new List<string>
        {
            "squid-tentacle register",
            $"--server \"{command.ServerUrl}\"",
            $"--api-key \"{apiKey}\"",
            $"--flavor LinuxTentacle"
        };

        if (!string.IsNullOrWhiteSpace(roles))
            registerArgs.Add($"--role \"{roles}\"");

        if (!string.IsNullOrWhiteSpace(environments))
            registerArgs.Add($"--environment \"{environments}\"");

        if (!string.IsNullOrWhiteSpace(command.MachineName))
            registerArgs.Add($"--name \"{command.MachineName}\"");

        if (isListening)
        {
            registerArgs.Add($"--listening-host \"{command.ListeningHostName}\"");
            registerArgs.Add($"--listening-port \"{command.ListeningPort}\"");

            if (!string.IsNullOrWhiteSpace(serverThumbprint))
                registerArgs.Add($"--server-cert \"{serverThumbprint}\"");
        }
        else
        {
            registerArgs.Add($"--comms-url \"{command.ServerCommsUrl}\"");
        }

        lines.Add(JoinLines(registerArgs.ToArray()));
        lines.Add("");

        // Step 3: Install service
        lines.Add("# Step 3: Install as systemd service");
        lines.Add("sudo squid-tentacle service install");

        return string.Join("\n", lines);
    }

    // ========================================================================
    // Docker Compose
    // ========================================================================

    private static string BuildTentacleDockerComposeScript(
        GenerateTentacleInstallScriptCommand command, string apiKey, bool isListening)
    {
        var image = string.IsNullOrWhiteSpace(command.DockerImage) ? DefaultTentacleImage : command.DockerImage;
        var roles = string.Join(",", command.Tags ?? []);
        var environments = string.Join(",", command.Environments ?? []);

        var lines = new List<string>
        {
            "services:",
            "  squid-tentacle:",
            $"    image: {image}",
            "    environment:",
            $"      Tentacle__ServerUrl: \"{command.ServerUrl}\"",
            $"      Tentacle__ApiKey: \"{apiKey}\"",
            $"      Tentacle__Roles: \"{roles}\"",
            $"      Tentacle__Environments: \"{environments}\"",
            $"      Tentacle__SpaceId: \"{command.SpaceId}\""
        };

        if (isListening)
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
