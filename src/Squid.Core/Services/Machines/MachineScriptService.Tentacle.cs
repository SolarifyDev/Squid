using System.Net;
using System.Security.Cryptography.X509Certificates;
using Squid.Core.Services.Machines.Scripts.Tentacle;
using Squid.Message.Commands.Machine;
using Squid.Message.Constants;

namespace Squid.Core.Services.Machines;

public partial class MachineScriptService
{
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

            var serverThumbprint = GetServerThumbprint();

            var context = new TentacleInstallContext
            {
                Command = command,
                ApiKey = apiKeyResult.ApiKey,
                ServerThumbprint = serverThumbprint,
                IsListening = IsListeningMode(command.CommunicationMode)
            };

            var scripts = BuildScripts(context);

            return Success<GenerateTentacleInstallScriptResponse, GenerateTentacleInstallScriptData>(
                new GenerateTentacleInstallScriptData
                {
                    ServerThumbprint = serverThumbprint,
                    Scripts = scripts
                });
        }
        catch (Exception ex)
        {
            return Fail<GenerateTentacleInstallScriptResponse, GenerateTentacleInstallScriptData>(
                HttpStatusCode.InternalServerError, ex.Message);
        }
    }

    private List<TentacleInstallScript> BuildScripts(TentacleInstallContext context)
    {
        var osFilter = context.Command.OperatingSystem;

        return _tentacleScriptBuilders
            .Where(b => string.IsNullOrWhiteSpace(osFilter)
                || b.OperatingSystem.Equals(osFilter, StringComparison.OrdinalIgnoreCase))
            .Select(b => b.Build(context))
            .ToList();
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
}
