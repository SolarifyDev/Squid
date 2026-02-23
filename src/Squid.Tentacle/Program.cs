using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Certificate;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Core;
using Squid.Tentacle.Halibut;
using Squid.Tentacle.Health;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();

    var tentacleSettings = new TentacleSettings();
    config.GetSection("Tentacle").Bind(tentacleSettings);

    var kubernetesSettings = new KubernetesSettings();
    config.GetSection("Kubernetes").Bind(kubernetesSettings);

    Log.Information(
        "Squid Tentacle starting. Flavor={Flavor}, ServerUrl={ServerUrl}, Namespace={Namespace}, UseScriptPods={UseScriptPods}",
        tentacleSettings.Flavor, tentacleSettings.ServerUrl, kubernetesSettings.Namespace, kubernetesSettings.UseScriptPods);

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

    var certManager = new TentacleCertificateManager(tentacleSettings.CertsPath);
    var tentacleCert = certManager.LoadOrCreateCertificate();
    var subscriptionId = certManager.LoadOrCreateSubscriptionId();

    Log.Information("Tentacle certificate thumbprint: {Thumbprint}", tentacleCert.Thumbprint);
    Log.Information("Tentacle subscription ID: {SubscriptionId}", subscriptionId);

    var flavorResolver = new TentacleFlavorResolver(TentacleFlavorCatalog.DiscoverBuiltInFlavors());
    var flavor = flavorResolver.Resolve(tentacleSettings.Flavor);
    var runtime = flavor.CreateRuntime(new TentacleFlavorContext
    {
        TentacleSettings = tentacleSettings,
        KubernetesSettings = kubernetesSettings
    });

    Log.Information("Tentacle flavor selected: {Flavor}", flavor.Id);

    var registration = await runtime.Registrar.RegisterAsync(
        new TentacleIdentity(subscriptionId, tentacleCert.Thumbprint), cts.Token).ConfigureAwait(false);

    Log.Information("Registered with server. MachineId={MachineId}, ServerThumbprint={ServerThumbprint}",
        registration.MachineId, registration.ServerThumbprint);
    
    var scriptService = new BackendScriptServiceAdapter(runtime.ScriptBackend);

    await using var halibutHost = new TentacleHalibutHost(tentacleCert, scriptService, tentacleSettings);
    halibutHost.StartPolling(registration.ServerThumbprint, subscriptionId, registration.SubscriptionUri);

    var isReady = true;
    await using var healthServer = new HealthCheckServer(tentacleSettings.HealthCheckPort, () => isReady);

    try
    {
        healthServer.Start();
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Failed to start health check server on port {Port}", tentacleSettings.HealthCheckPort);
    }

    await RunStartupHooksAsync(runtime.StartupHooks, cts.Token).ConfigureAwait(false);

    Log.Information("Squid Tentacle running. SubscriptionId={SubscriptionId}. Press Ctrl+C to stop.", subscriptionId);

    StartBackgroundTasks(runtime.BackgroundTasks, cts.Token);

    await WaitForShutdownAsync(cts.Token).ConfigureAwait(false);

    Log.Information("Squid Tentacle shutting down gracefully");
}
catch (OperationCanceledException)
{
    Log.Information("Squid Tentacle shutdown requested");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Squid Tentacle terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

static async Task WaitForShutdownAsync(CancellationToken ct)
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

static async Task RunStartupHooksAsync(
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

static void StartBackgroundTasks(
    IReadOnlyList<ITentacleBackgroundTask> tasks,
    CancellationToken ct)
{
    foreach (var task in tasks)
    {
        _ = Task.Run(() => RunBackgroundTaskAsync(task, ct), ct);
        Log.Information("Tentacle background task started: {TaskName}", task.Name);
    }
}

static async Task RunBackgroundTaskAsync(
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
