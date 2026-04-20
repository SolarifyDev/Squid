using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Certificate;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Core;
using Squid.Tentacle.Instance;
using Serilog;

namespace Squid.Tentacle.Commands;

/// <summary>
/// One-shot registration: resolves the flavor's registrar, calls RegisterAsync,
/// **persists the effective settings to the instance config file**, then exits.
///
/// The persistence step is what makes <c>service install</c> + <c>systemd start</c>
/// actually work: systemd re-invokes <c>squid-tentacle run</c> with no arguments,
/// so the agent can only come back up if its settings are already on disk.
///
/// Shorthand args are mapped to TentacleSettings before execution:
///   --server URL         → Tentacle:ServerUrl
///   --api-key KEY        → Tentacle:ApiKey
///   --role ROLE          → Tentacle:Roles
///   --environment ENV    → Tentacle:Environments
///   --comms-url URL      → Tentacle:ServerCommsUrl
///   --flavor NAME        → Tentacle:Flavor
///   --instance NAME      → picked up by the outer ConfigurationBuilder and Program.cs
/// </summary>
public sealed class RegisterCommand : ITentacleCommand
{
    public string Name => "register";
    public string Description => "Register this Tentacle with the Squid Server and persist its config";

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
        ["--listening-host"] = "Tentacle:ListeningHostName",
        ["--listening-port"] = "Tentacle:ListeningPort",
        ["--server-cert"] = "Tentacle:ServerCertificate",
        ["--public-hostname"] = "Tentacle:PublicHostNameConfiguration",
        ["--proxy-host"] = "Tentacle:Proxy:Host",
        ["--proxy-port"] = "Tentacle:Proxy:Port",
        ["--proxy-user"] = "Tentacle:Proxy:Username",
        ["--proxy-password"] = "Tentacle:Proxy:Password",
    };

    /// <summary>Settings we deem safe + necessary to persist after a successful register.</summary>
    private static readonly string[] PersistableKeys =
    [
        "Tentacle:Flavor",
        "Tentacle:ServerUrl",
        "Tentacle:ServerCommsUrl",
        "Tentacle:ServerCommsAddresses",
        "Tentacle:ServerCertificate",
        "Tentacle:MachineName",
        "Tentacle:Roles",
        "Tentacle:Environments",
        "Tentacle:ListeningHostName",
        "Tentacle:ListeningPort",
        "Tentacle:SpaceId",
        "Tentacle:SubscriptionId",
        "Tentacle:CertsPath",
        "Tentacle:WorkspacePath",
    ];

    public async Task<int> ExecuteAsync(string[] args, IConfiguration config, CancellationToken ct)
    {
        // --instance is consumed upstream in Program.cs — this extraction is here so that
        // inlining the shorthand-expanded args into IConfigurationBuilder.AddCommandLine doesn't
        // choke, and so we know which instance to persist into.
        var (instanceName, argsWithoutInstance) = InstanceSelector.ExtractInstanceArg(args);
        var instance = InstanceSelector.Resolve(instanceName);

        var expandedArgs = ExpandShorthandArgs(argsWithoutInstance);

        // Per-instance certs path takes priority over whatever's in config, so multi-instance
        // setups don't clobber each other's certificates.
        var instanceCertsPath = InstanceSelector.ResolveCertsPath(instance);

        var merged = new ConfigurationBuilder()
            .AddConfiguration(config)
            .AddCommandLine(expandedArgs)
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Tentacle:CertsPath"] = instanceCertsPath
            })
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

        PersistInstanceConfig(instance, settings, subscriptionId, registration.ServerThumbprint);

        // Hand the just-persisted config + certs over to the systemd service
        // user. Without this step, `sudo register` leaves root:root 0600 files
        // that the `squid-tentacle` service user can't read, and
        // `systemctl start squid-tentacle` crashes with PermissionDenied. No-op
        // outside the root-on-Linux-with-service-user case — see
        // InstanceOwnershipHandover's doc comment for the full matrix.
        var handover = new InstanceOwnershipHandover().HandOver(instance, Path.GetDirectoryName(instanceCertsPath));

        if (handover.DidHandOver)
            Log.Information("Handed ownership of instance artifacts to {User}: {Paths}", handover.ServiceUser, string.Join(", ", handover.Paths));
        else
            Log.Information("Skipped ownership handover: {Reason}", handover.Reason);

        Console.WriteLine($"MachineId:       {registration.MachineId}");
        Console.WriteLine($"ServerThumbprint: {registration.ServerThumbprint}");
        Console.WriteLine($"SubscriptionUri: {registration.SubscriptionUri}");
        Console.WriteLine($"Thumbprint:      {cert.Thumbprint}");
        Console.WriteLine($"Instance:        {instance.Name}");
        Console.WriteLine($"Config saved to: {instance.ConfigPath}");
        Console.WriteLine("Registration complete.");

        return 0;
    }

    /// <summary>
    /// Writes the effective settings to the instance's config.json so that a subsequent
    /// <c>squid-tentacle run</c> (e.g. from systemd) can pick them up.
    /// Falls back gracefully if registration wasn't created via <c>create-instance</c>:
    /// ensures the Default entry in <c>instances.json</c> exists.
    /// </summary>
    private static void PersistInstanceConfig(InstanceRecord instance, Configuration.TentacleSettings settings, string subscriptionId, string serverThumbprint)
    {
        // Auto-create Default instance in the registry if this is a first-run where the user
        // skipped create-instance and went straight to register.
        try
        {
            var registry = InstanceRegistry.CreateForCurrentProcess();

            if (registry.Find(instance.Name) == null)
                registry.Add(new InstanceRecord { Name = instance.Name, ConfigPath = instance.ConfigPath });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not update instance registry (non-fatal — config still persisted directly)");
        }

        var file = new TentacleConfigFile(instance.ConfigPath);

        var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Tentacle:Flavor"] = settings.Flavor,
            ["Tentacle:ServerUrl"] = settings.ServerUrl,
            ["Tentacle:ServerCommsUrl"] = settings.ServerCommsUrl,
            ["Tentacle:ServerCommsAddresses"] = settings.ServerCommsAddresses,
            ["Tentacle:ServerCertificate"] = string.IsNullOrWhiteSpace(settings.ServerCertificate) ? serverThumbprint : settings.ServerCertificate,
            ["Tentacle:MachineName"] = settings.MachineName,
            ["Tentacle:Roles"] = settings.Roles,
            ["Tentacle:Environments"] = settings.Environments,
            ["Tentacle:ListeningHostName"] = settings.ListeningHostName,
            ["Tentacle:ListeningPort"] = settings.ListeningPort.ToString(),
            ["Tentacle:PublicHostNameConfiguration"] = settings.PublicHostNameConfiguration,
            ["Tentacle:Proxy:Host"] = settings.Proxy?.Host,
            ["Tentacle:Proxy:Port"] = settings.Proxy?.Port > 0 ? settings.Proxy.Port.ToString() : null,
            ["Tentacle:Proxy:Username"] = settings.Proxy?.Username,
            ["Tentacle:Proxy:Password"] = settings.Proxy?.Password,
            ["Tentacle:SpaceId"] = settings.SpaceId.ToString(),
            ["Tentacle:SubscriptionId"] = subscriptionId,
            ["Tentacle:CertsPath"] = settings.CertsPath,
            ["Tentacle:WorkspacePath"] = settings.WorkspacePath,
            ["Tentacle:Registered"] = "true",
        };

        file.Merge(updates);

        Log.Information("Persisted settings to {Path}", instance.ConfigPath);
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
