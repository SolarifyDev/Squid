using k8s;
using Microsoft.Extensions.Configuration;
using Squid.Agent.Certificate;
using Squid.Agent.Configuration;
using Squid.Agent.Halibut;
using Squid.Agent.Health;
using Squid.Agent.Kubernetes;
using Squid.Agent.Registration;
using Squid.Agent.ScriptExecution;
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

    var settings = new AgentSettings();
    config.GetSection("Agent").Bind(settings);

    Log.Information("Squid Agent starting. ServerUrl={ServerUrl}, Namespace={Namespace}, UseScriptPods={UseScriptPods}",
        settings.ServerUrl, settings.Namespace, settings.UseScriptPods);

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

    var certManager = new AgentCertificateManager(settings.CertsPath);
    var agentCert = certManager.LoadOrCreateCertificate();
    var subscriptionId = certManager.LoadOrCreateSubscriptionId();

    Log.Information("Agent certificate thumbprint: {Thumbprint}", agentCert.Thumbprint);
    Log.Information("Agent subscription ID: {SubscriptionId}", subscriptionId);

    var registrationClient = new AgentRegistrationClient(settings);

    var registration = await registrationClient.RegisterAsync(
        subscriptionId, agentCert.Thumbprint, cts.Token).ConfigureAwait(false);

    Log.Information("Registered with server. MachineId={MachineId}, ServerThumbprint={ServerThumbprint}",
        registration.MachineId, registration.ServerThumbprint);

    var scriptService = CreateScriptService(settings, out var podMonitor);

    await using var halibutHost = new AgentHalibutHost(agentCert, scriptService, settings);
    halibutHost.StartPolling(registration.ServerThumbprint, subscriptionId);

    var isReady = true;
    await using var healthServer = new HealthCheckServer(settings.HealthCheckPort, () => isReady);

    try
    {
        healthServer.Start();
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Failed to start health check server on port {Port}", settings.HealthCheckPort);
    }

    TouchInitializationFlag();

    Log.Information("Squid Agent running. SubscriptionId={SubscriptionId}. Press Ctrl+C to stop.", subscriptionId);

    if (podMonitor != null)
    {
        _ = Task.Run(() => podMonitor.RunAsync(cts.Token), cts.Token);
        Log.Information("Pod monitor started");
    }

    await WaitForShutdownAsync(cts.Token).ConfigureAwait(false);

    Log.Information("Squid Agent shutting down gracefully");
}
catch (OperationCanceledException)
{
    Log.Information("Squid Agent shutdown requested");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Squid Agent terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

static IScriptService CreateScriptService(AgentSettings settings, out KubernetesPodMonitor? podMonitor)
{
    if (!settings.UseScriptPods)
    {
        Log.Information("Using local script execution mode");
        podMonitor = null;
        return new LocalScriptService();
    }

    Log.Information("Using Script Pod execution mode. Image={Image}", settings.ScriptPodImage);

    var k8sConfig = KubernetesClientConfiguration.IsInCluster()
        ? KubernetesClientConfiguration.InClusterConfig()
        : KubernetesClientConfiguration.BuildConfigFromConfigFile();

    var k8sClient = new k8s.Kubernetes(k8sConfig);
    var podOps = new KubernetesPodOperations(k8sClient);

    var podMgr = new KubernetesPodManager(podOps, settings);
    var service = new ScriptPodService(settings, podMgr);

    podMonitor = new KubernetesPodMonitor(podMgr, service, settings);

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
