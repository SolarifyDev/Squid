using System.Security.Cryptography.X509Certificates;
using Squid.Core.Settings.Halibut;
using Squid.Core.Settings.Server;
using Squid.Core.Settings.SelfCert;
using Microsoft.AspNetCore.Authorization;

namespace Squid.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/server-configuration")]
public class ServerConfigurationController : ControllerBase
{
    private readonly ServerUrlSetting _serverUrlSetting;
    private readonly HalibutSetting _halibutSetting;
    private readonly SelfCertSetting _selfCertSetting;

    public ServerConfigurationController(ServerUrlSetting serverUrlSetting, HalibutSetting halibutSetting, SelfCertSetting selfCertSetting)
    {
        _serverUrlSetting = serverUrlSetting;
        _halibutSetting = halibutSetting;
        _selfCertSetting = selfCertSetting;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        var serverUrl = _serverUrlSetting.ExternalUrl;
        var commsUrl = ResolveCommsUrl(serverUrl);
        var thumbprint = ResolveThumbprint();

        return Ok(new ServerConfigurationResponse
        {
            ServerUrl = serverUrl,
            CommsUrl = commsUrl,
            ServerThumbprint = thumbprint,
            PollingEnabled = _halibutSetting.Polling.Enabled,
            PollingPort = _halibutSetting.Polling.Port
        });
    }

    private string ResolveCommsUrl(string serverUrl)
    {
        if (!string.IsNullOrWhiteSpace(_serverUrlSetting.CommsUrl))
            return _serverUrlSetting.CommsUrl;

        if (string.IsNullOrWhiteSpace(serverUrl))
            return string.Empty;

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
            return string.Empty;

        return $"https://{uri.Host}:{_halibutSetting.Polling.Port}/";
    }

    private string ResolveThumbprint()
    {
        if (string.IsNullOrWhiteSpace(_selfCertSetting.Base64))
            return string.Empty;

        var certBytes = Convert.FromBase64String(_selfCertSetting.Base64);
        var cert = X509CertificateLoader.LoadPkcs12(certBytes, _selfCertSetting.Password, X509KeyStorageFlags.MachineKeySet);

        return cert.Thumbprint;
    }
}

public class ServerConfigurationResponse
{
    public string ServerUrl { get; set; }
    public string CommsUrl { get; set; }
    public string ServerThumbprint { get; set; }
    public bool PollingEnabled { get; set; }
    public int PollingPort { get; set; }
}
