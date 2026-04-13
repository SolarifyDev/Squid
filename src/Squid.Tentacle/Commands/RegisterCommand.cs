using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Certificate;
using Squid.Tentacle.Core;
using Serilog;

namespace Squid.Tentacle.Commands;

/// <summary>
/// One-shot registration: resolves the flavor's registrar, calls RegisterAsync, prints the result, then exits.
/// This is generic — works for any flavor (LinuxTentacle, KubernetesAgent, future WindowsTentacle).
///
/// Shorthand args are mapped to TentacleSettings before execution:
///   --server URL      → Tentacle:ServerUrl
///   --api-key KEY     → Tentacle:ApiKey
///   --role ROLE       → Tentacle:Roles
///   --environment ENV → Tentacle:Environments
///   --comms-url URL   → Tentacle:ServerCommsUrl
///   --flavor NAME     → Tentacle:Flavor
/// </summary>
public sealed class RegisterCommand : ITentacleCommand
{
    public string Name => "register";
    public string Description => "Register this Tentacle with the Squid Server (one-shot)";

    private static readonly Dictionary<string, string> ArgMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--server"] = "Tentacle:ServerUrl",
        ["--api-key"] = "Tentacle:ApiKey",
        ["--bearer-token"] = "Tentacle:BearerToken",
        ["--role"] = "Tentacle:Roles",
        ["--environment"] = "Tentacle:Environments",
        ["--comms-url"] = "Tentacle:ServerCommsUrl",
        ["--flavor"] = "Tentacle:Flavor",
        ["--name"] = "Tentacle:MachineName",
    };

    public async Task<int> ExecuteAsync(string[] args, IConfiguration config, CancellationToken ct)
    {
        var expandedArgs = ExpandShorthandArgs(args);

        var merged = new ConfigurationBuilder()
            .AddConfiguration(config)
            .AddCommandLine(expandedArgs)
            .Build();

        var settings = TentacleApp.LoadTentacleSettings(merged);

        if (string.IsNullOrWhiteSpace(settings.ServerUrl) || settings.ServerUrl == "https://localhost:7078")
        {
            Console.Error.WriteLine("Error: --server is required");
            Console.Error.WriteLine("Usage: squid-tentacle register --server URL --api-key KEY --role ROLE --environment ENV [--comms-url URL]");
            return 1;
        }

        var certManager = new TentacleCertificateManager(settings.CertsPath);
        var cert = certManager.LoadOrCreateCertificate();
        var subscriptionId = certManager.LoadOrCreateSubscriptionId(settings.SubscriptionId);

        var identity = new TentacleIdentity(subscriptionId, cert.Thumbprint);

        var flavorResolver = new TentacleFlavorResolver(TentacleFlavorCatalog.DiscoverBuiltInFlavors());
        var flavor = flavorResolver.Resolve(settings.Flavor);
        var runtime = flavor.CreateRuntime(new TentacleFlavorContext
        {
            TentacleSettings = settings,
            Configuration = merged
        });

        Log.Information("Registering with {ServerUrl} as flavor {Flavor}...", settings.ServerUrl, flavor.Id);

        var registration = await runtime.Registrar.RegisterAsync(identity, ct).ConfigureAwait(false);

        Console.WriteLine($"MachineId:       {registration.MachineId}");
        Console.WriteLine($"ServerThumbprint: {registration.ServerThumbprint}");
        Console.WriteLine($"SubscriptionUri: {registration.SubscriptionUri}");
        Console.WriteLine($"Thumbprint:      {cert.Thumbprint}");
        Console.WriteLine("Registration complete.");

        return 0;
    }

    internal static string[] ExpandShorthandArgs(string[] args)
    {
        var result = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            if (ArgMapping.TryGetValue(args[i], out var configKey) && i + 1 < args.Length)
            {
                result.Add($"--{configKey}={args[i + 1]}");
                i++;
            }
            else
            {
                result.Add(args[i]);
            }
        }

        return result.ToArray();
    }
}
