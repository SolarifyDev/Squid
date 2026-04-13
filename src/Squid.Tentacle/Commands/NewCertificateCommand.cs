using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Certificate;
using Squid.Tentacle.Core;

namespace Squid.Tentacle.Commands;

public sealed class NewCertificateCommand : ITentacleCommand
{
    public string Name => "new-certificate";
    public string Description => "Generate a new Tentacle certificate (if one does not exist)";

    public Task<int> ExecuteAsync(string[] args, IConfiguration config, CancellationToken ct)
    {
        var settings = TentacleApp.LoadTentacleSettings(config);
        var certManager = new TentacleCertificateManager(settings.CertsPath);
        var cert = certManager.LoadOrCreateCertificate();
        var subscriptionId = certManager.LoadOrCreateSubscriptionId(settings.SubscriptionId);

        Console.WriteLine($"Thumbprint:     {cert.Thumbprint}");
        Console.WriteLine($"SubscriptionId: {subscriptionId}");
        Console.WriteLine($"CertsPath:      {settings.CertsPath}");

        return Task.FromResult(0);
    }
}
