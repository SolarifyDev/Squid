using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Certificate;
using Squid.Tentacle.Core;

namespace Squid.Tentacle.Commands;

public sealed class ShowThumbprintCommand : ITentacleCommand
{
    public string Name => "show-thumbprint";
    public string Description => "Display the Tentacle certificate thumbprint";

    public Task<int> ExecuteAsync(string[] args, IConfiguration config, CancellationToken ct)
    {
        var settings = TentacleApp.LoadTentacleSettings(config);
        var certManager = new TentacleCertificateManager(settings.CertsPath);
        var cert = certManager.LoadOrCreateCertificate();

        Console.WriteLine(cert.Thumbprint);

        return Task.FromResult(0);
    }
}
