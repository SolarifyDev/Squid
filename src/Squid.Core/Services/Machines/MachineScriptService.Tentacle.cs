using System.Net;
using System.Security.Cryptography.X509Certificates;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Machines.Scripts.Tentacle;
using Squid.Message.Commands.Machine;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Requests.Machines;

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

            var probe = await ProbePollingCommsUrlAsync(command, serverThumbprint, ct).ConfigureAwait(false);

            var downloads = await BuildDownloadsAsync(command.OperatingSystem, ct).ConfigureAwait(false);

            return Success<GenerateTentacleInstallScriptResponse, GenerateTentacleInstallScriptData>(
                new GenerateTentacleInstallScriptData
                {
                    ServerThumbprint = serverThumbprint,
                    Scripts = scripts,
                    CommsUrlProbe = probe,
                    Downloads = downloads
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

    /// <summary>
    /// Builds the per-OS / per-architecture archive download list bundled
    /// alongside the install scripts. Filters by the same <c>OperatingSystem</c>
    /// the script builder honoured so the FE sees a consistent view (Linux
    /// scripts → Linux downloads only, etc.). Always uses "latest" version
    /// — the install-script flow has no Version pin (the script downloads
    /// whatever's latest at the moment the operator runs it on the agent
    /// host); operators who need a specific version pin the standalone
    /// <c>GET /tentacle-downloads?version=...</c> endpoint instead.
    /// </summary>
    private async Task<List<TentacleDownloadDto>> BuildDownloadsAsync(string osFilter, CancellationToken ct)
    {
        var normalisedFilter = TentacleDownloadCatalog.NormaliseOsFilter(osFilter);
        var downloads = new List<TentacleDownloadDto>();

        if (TentacleDownloadCatalog.ShouldIncludeWindows(normalisedFilter))
        {
            var version = await ResolveLatestVersionAsync(AgentOperatingSystems.Windows, ct).ConfigureAwait(false);
            downloads.AddRange(TentacleDownloadCatalog.BuildWindows(version));
        }

        if (TentacleDownloadCatalog.ShouldIncludeLinux(normalisedFilter))
        {
            var version = await ResolveLatestVersionAsync(AgentOperatingSystems.Linux, ct).ConfigureAwait(false);
            downloads.AddRange(TentacleDownloadCatalog.BuildLinux(version));
        }

        return downloads;
    }

    private Task<string> ResolveLatestVersionAsync(string os, CancellationToken ct) =>
        _tentacleVersionRegistry.GetLatestVersionAsync(
            nameof(CommunicationStyle.TentaclePolling),
            new MachineRuntimeCapabilities { Os = os },
            ct);

    /// <summary>
    /// Canonical description for the SHARED Tentacle bootstrap API key. Pinned in
    /// <see cref="TentacleBootstrapKeyDescription"/> so the rotation endpoint
    /// (system admin) and the script generator agree on which row to look up.
    ///
    /// <para>Single instance per server: <see cref="TryCreateTentacleApiKeyAsync"/>
    /// always queries by this description first and reuses the existing key when
    /// present. New keys are minted only on first install + after rotation. The
    /// DB accumulates AT MOST ONE active Tentacle bootstrap key per server.</para>
    /// </summary>
    internal const string TentacleBootstrapKeyDescription = "Tentacle install bootstrap (system-shared, rotate via admin endpoint)";

    private async Task<(bool Success, string ApiKey, HttpStatusCode Code, string Message)> TryCreateTentacleApiKeyAsync(CancellationToken ct)
    {
        var existing = await _accountService.FindApiKeyByDescriptionAsync(CurrentUsers.InternalUser.Id, TentacleBootstrapKeyDescription, ct).ConfigureAwait(false);

        if (existing != null) return (true, existing.ApiKey, HttpStatusCode.OK, null);

        var result = await _accountService.CreateApiKeyAsync(CurrentUsers.InternalUser.Id, TentacleBootstrapKeyDescription, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result?.ApiKey))
            return (false, null, HttpStatusCode.InternalServerError, "Failed to create Tentacle bootstrap API key");

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

    /// <summary>
    /// Polling-mode install scripts embed a <c>ServerCommsUrl</c> that must be
    /// externally reachable with a valid TLS endpoint presenting the server's
    /// Halibut cert. Probing here (at script-generation time) surfaces
    /// networking/SLB misconfigurations to the operator before the script
    /// reaches a new machine owner — avoids repeating the 2026-04-18 incident
    /// where mis-configured SLB health checks led to days of silent EOF loops.
    /// Listening-mode registrations have no polling URL, so we skip.
    /// </summary>
    private async Task<TentacleCommsProbeInfo> ProbePollingCommsUrlAsync(
        GenerateTentacleInstallScriptCommand command, string serverThumbprint, CancellationToken ct)
    {
        if (IsListeningMode(command.CommunicationMode))
            return new TentacleCommsProbeInfo { Skipped = true, Detail = "Listening-mode registration: no polling URL to probe" };

        var probe = await _commsUrlProbe
            .ProbeAsync(command.ServerCommsUrl, serverThumbprint, ct)
            .ConfigureAwait(false);

        return ToTransportDto(probe);
    }

    /// <summary>Map domain probe result → transport DTO consumed by UI/clients.</summary>
    private static TentacleCommsProbeInfo ToTransportDto(TentacleCommsProbeResult probe) => new()
    {
        Reachable = probe.Reachable,
        Skipped = probe.Skipped,
        ObservedThumbprint = probe.ObservedThumbprint ?? string.Empty,
        ThumbprintMatches = probe.ThumbprintMatches,
        Detail = probe.Detail ?? string.Empty
    };
}
