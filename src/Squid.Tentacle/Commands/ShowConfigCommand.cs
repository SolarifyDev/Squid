using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Certificate;
using Squid.Tentacle.Core;
using Serilog;

namespace Squid.Tentacle.Commands;

public sealed class ShowConfigCommand : ITentacleCommand
{
    public string Name => "show-config";
    public string Description => "Display the resolved Tentacle configuration";

    public Task<int> ExecuteAsync(string[] args, IConfiguration config, CancellationToken ct)
    {
        var settings = TentacleApp.LoadTentacleSettings(config);

        Console.WriteLine("=== Squid Tentacle Configuration ===");
        Console.WriteLine($"Flavor:              {(string.IsNullOrEmpty(settings.Flavor) ? "(not set)" : settings.Flavor)}");
        Console.WriteLine($"ServerUrl:           {settings.ServerUrl}");
        Console.WriteLine($"ServerCommsUrl:      {(string.IsNullOrEmpty(settings.ServerCommsUrl) ? "(not set — Listening mode)" : settings.ServerCommsUrl)}");
        Console.WriteLine($"Roles:               {(string.IsNullOrEmpty(settings.Roles) ? "(not set)" : settings.Roles)}");
        Console.WriteLine($"Environments:        {(string.IsNullOrEmpty(settings.Environments) ? "(not set)" : settings.Environments)}");
        Console.WriteLine($"WorkspacePath:       {settings.WorkspacePath}");
        Console.WriteLine($"CertsPath:           {settings.CertsPath}");
        Console.WriteLine($"HealthCheckPort:     {settings.HealthCheckPort}");
        Console.WriteLine($"ListeningPort:       {settings.ListeningPort}");
        Console.WriteLine($"PollingConnections:  {settings.PollingConnectionCount}");
        Console.WriteLine($"DrainTimeout:        {settings.ShutdownDrainTimeoutSeconds}s");

        var mode = string.IsNullOrWhiteSpace(settings.ServerCommsUrl)
            && string.IsNullOrWhiteSpace(settings.ServerCommsAddresses)
            ? "Listening" : "Polling";
        Console.WriteLine($"Detected Mode:       {mode}");

        try
        {
            var certManager = new TentacleCertificateManager(settings.CertsPath);
            var cert = certManager.LoadOrCreateCertificate();
            var subscriptionId = certManager.LoadOrCreateSubscriptionId(settings.SubscriptionId);

            Console.WriteLine($"Thumbprint:          {cert.Thumbprint}");
            Console.WriteLine($"SubscriptionId:      {subscriptionId}");

            var daysToExpiry = (int)(cert.NotAfter - DateTime.UtcNow).TotalDays;
            var expiryWarning = daysToExpiry < 180
                ? $" ⚠️  expires in {daysToExpiry} day(s) — run 'squid-tentacle new-certificate' and re-register"
                : string.Empty;
            Console.WriteLine($"CertExpires:         {cert.NotAfter:yyyy-MM-dd} ({daysToExpiry} days){expiryWarning}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Certificate:         Error — {ex.Message}");
        }

        return Task.FromResult(0);
    }
}
