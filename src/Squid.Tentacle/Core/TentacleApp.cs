using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Certificate;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Halibut;
using Squid.Tentacle.Health;
using Serilog;

namespace Squid.Tentacle.Core;

public sealed class TentacleApp
{
    private readonly TentacleAppDependencies _dependencies;

    public TentacleApp()
        : this(TentacleAppDependencies.CreateDefault())
    {
    }

    public TentacleApp(TentacleAppDependencies dependencies)
    {
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
    }

    public async Task RunAsync(
        TentacleSettings tentacleSettings,
        IConfiguration configuration,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tentacleSettings);
        ArgumentNullException.ThrowIfNull(configuration);

        Log.Information(
            "Squid Tentacle starting. Flavor={Flavor}, ServerUrl={ServerUrl}",
            tentacleSettings.Flavor, tentacleSettings.ServerUrl);

        var certManager = _dependencies.CertificateManagerFactory(tentacleSettings.CertsPath);
        var tentacleCert = certManager.LoadOrCreateCertificate();
        var subscriptionId = certManager.LoadOrCreateSubscriptionId(tentacleSettings.SubscriptionId);

        Log.Information("Tentacle certificate thumbprint: {Thumbprint}", tentacleCert.Thumbprint);
        Log.Information("Tentacle subscription ID: {SubscriptionId}", subscriptionId);

        var flavorResolver = _dependencies.FlavorResolverFactory(_dependencies.BuiltInFlavorsProvider());
        var flavor = flavorResolver.Resolve(tentacleSettings.Flavor);
        var runtime = flavor.CreateRuntime(new TentacleFlavorContext
        {
            TentacleSettings = tentacleSettings,
            Configuration = configuration
        });

        Log.Information("Tentacle flavor selected: {Flavor}", flavor.Id);

        var registration = await runtime.Registrar.RegisterAsync(
            new TentacleIdentity(subscriptionId, tentacleCert.Thumbprint), ct).ConfigureAwait(false);

        Log.Information("Registered with server. MachineId={MachineId}, ServerThumbprint={ServerThumbprint}",
            registration.MachineId, registration.ServerThumbprint);

        var scriptService = new BackendScriptServiceAdapter(runtime.ScriptBackend);
        var capabilitiesService = new CapabilitiesService(runtime.Metadata);

        await using var halibutHost = _dependencies.HalibutHostFactory(tentacleCert, scriptService, tentacleSettings, capabilitiesService);

        if (runtime.CommunicationMode == TentacleCommunicationMode.Polling)
        {
            if (string.IsNullOrWhiteSpace(tentacleSettings.ServerCommsUrl))
                throw new InvalidOperationException(
                    "ServerCommsUrl is required for polling mode. Set it to the Halibut polling endpoint (e.g., https://server:10943)");

            halibutHost.StartPolling(registration.ServerThumbprint, subscriptionId, registration.SubscriptionUri);
        }
        else
        {
            var listeningPort = runtime.ListeningPort ?? tentacleSettings.ListeningPort;
            halibutHost.StartListening(listeningPort, registration.ServerThumbprint);
        }

        var isReady = true;
        Func<bool> readinessCheck = runtime.ReadinessCheck != null
            ? () => isReady && runtime.ReadinessCheck()
            : () => isReady;

        await using var healthServer = _dependencies.HealthCheckServerFactory(tentacleSettings.HealthCheckPort, readinessCheck);

        try
        {
            healthServer.Start();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to start health check server on port {Port}", tentacleSettings.HealthCheckPort);
        }

        await RunStartupHooksAsync(runtime.StartupHooks, ct).ConfigureAwait(false);

        Log.Information("Squid Tentacle running. SubscriptionId={SubscriptionId}. Press Ctrl+C to stop.", subscriptionId);

        StartBackgroundTasks(runtime.BackgroundTasks, ct);

        await WaitForShutdownAsync(ct).ConfigureAwait(false);

        Log.Information("Squid Tentacle shutting down — entering drain phase");
        isReady = false;

        if (runtime.ScriptBackend is IGracefulShutdownAware drainable)
        {
            var drainTimeout = TimeSpan.FromSeconds(tentacleSettings.ShutdownDrainTimeoutSeconds);
            await drainable.WaitForDrainAsync(drainTimeout).ConfigureAwait(false);
        }

        Log.Information("Squid Tentacle shutdown complete");
    }

    public static TentacleSettings LoadTentacleSettings(IConfiguration configuration)
    {
        var tentacleSettings = new TentacleSettings();
        configuration.GetSection("Tentacle").Bind(tentacleSettings);
        return tentacleSettings;
    }

    private static async Task WaitForShutdownAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    private static async Task RunStartupHooksAsync(
        IReadOnlyList<ITentacleStartupHook> hooks,
        CancellationToken ct)
    {
        foreach (var hook in hooks)
        {
            ct.ThrowIfCancellationRequested();

            await hook.RunAsync(ct).ConfigureAwait(false);
            Log.Information("Tentacle startup hook executed: {HookName}", hook.Name);
        }
    }

    private static void StartBackgroundTasks(
        IReadOnlyList<ITentacleBackgroundTask> tasks,
        CancellationToken ct)
    {
        foreach (var task in tasks)
        {
            _ = Task.Run(() => RunBackgroundTaskAsync(task, ct), ct);
            Log.Information("Tentacle background task started: {TaskName}", task.Name);
        }
    }

    private static async Task RunBackgroundTaskAsync(
        ITentacleBackgroundTask task,
        CancellationToken ct)
    {
        try
        {
            await task.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Tentacle background task failed: {TaskName}", task.Name);
        }
    }
}

public sealed class TentacleAppDependencies
{
    public Func<string, ITentacleCertificateManager> CertificateManagerFactory { get; init; } =
        certsPath => new TentacleCertificateManager(certsPath);

    public Func<IEnumerable<ITentacleFlavor>> BuiltInFlavorsProvider { get; init; } =
        TentacleFlavorCatalog.DiscoverBuiltInFlavors;

    public Func<IEnumerable<ITentacleFlavor>, TentacleFlavorResolver> FlavorResolverFactory { get; init; } =
        flavors => new TentacleFlavorResolver(flavors);

    public Func<X509Certificate2, Squid.Message.Contracts.Tentacle.IScriptService, TentacleSettings, Squid.Message.Contracts.Tentacle.ICapabilitiesService, ITentacleHalibutHost> HalibutHostFactory { get; init; } =
        (cert, scriptService, settings, capabilitiesService) => new TentacleHalibutHost(cert, scriptService, settings, capabilitiesService);

    public Func<int, Func<bool>, IHealthCheckServer> HealthCheckServerFactory { get; init; } =
        (port, readiness) => new HealthCheckServer(port, readiness);

    public static TentacleAppDependencies CreateDefault() => new();
}
