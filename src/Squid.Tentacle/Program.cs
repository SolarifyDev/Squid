using k8s;
using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Certificate;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Halibut;
using Squid.Tentacle.Health;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.Registration;
using Squid.Tentacle.ScriptExecution;
using Squid.Message.Contracts.Tentacle;
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

    Log.Information("Squid Tentacle starting. ServerUrl={ServerUrl}, Namespace={Namespace}, UseScriptPods={UseScriptPods}",
        tentacleSettings.ServerUrl, kubernetesSettings.Namespace, kubernetesSettings.UseScriptPods);

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

    var certManager = new TentacleCertificateManager(tentacleSettings.CertsPath);
    var tentacleCert = certManager.LoadOrCreateCertificate();
    var subscriptionId = certManager.LoadOrCreateSubscriptionId();

    Log.Information("Tentacle certificate thumbprint: {Thumbprint}", tentacleCert.Thumbprint);
    Log.Information("Tentacle subscription ID: {SubscriptionId}", subscriptionId);

    var registrationClient = new TentacleRegistrationClient(tentacleSettings, kubernetesSettings);

    var registration = await registrationClient.RegisterAsync(
        subscriptionId, tentacleCert.Thumbprint, cts.Token).ConfigureAwait(false);

    Log.Information("Registered with server. MachineId={MachineId}, ServerThumbprint={ServerThumbprint}",
        registration.MachineId, registration.ServerThumbprint);

    var scriptService = CreateScriptService(tentacleSettings, kubernetesSettings, out var podMonitor);

    await using var halibutHost = new TentacleHalibutHost(tentacleCert, scriptService, tentacleSettings);
    halibutHost.StartPolling(registration.ServerThumbprint, subscriptionId);

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

    TouchInitializationFlag();

    Log.Information("Squid Tentacle running. SubscriptionId={SubscriptionId}. Press Ctrl+C to stop.", subscriptionId);

    if (podMonitor != null)
    {
        _ = Task.Run(() => podMonitor.RunAsync(cts.Token), cts.Token);
        Log.Information("Pod monitor started");
    }

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

static IScriptService CreateScriptService(
    TentacleSettings tentacleSettings,
    KubernetesSettings kubernetesSettings,
    out KubernetesPodMonitor? podMonitor)
{
    if (!kubernetesSettings.UseScriptPods)
    {
        Log.Information("Using local script execution mode");
        podMonitor = null;
        return new LocalScriptService();
    }

    Log.Information("Using Script Pod execution mode. Image={Image}", kubernetesSettings.ScriptPodImage);

    var k8sConfig = KubernetesClientConfiguration.IsInCluster()
        ? KubernetesClientConfiguration.InClusterConfig()
        : KubernetesClientConfiguration.BuildConfigFromConfigFile();

    var k8sClient = new k8s.Kubernetes(k8sConfig);
    var podOps = new KubernetesPodOperations(k8sClient);

    var podMgr = new KubernetesPodManager(podOps, kubernetesSettings);
    var service = new ScriptPodService(tentacleSettings, kubernetesSettings, podMgr);

    podMonitor = new KubernetesPodMonitor(podMgr, service, tentacleSettings);

    return service;
}

static void TouchInitializationFlag()
{
    const string flagPath = "/squid/initialized";

    try
    {
        var dir = Path.GetDirectoryName(flagPath);

        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            File.Create(flagPath).Dispose();
    }
    catch
    {
        // Not running in K8s — skip
    }
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
