using k8s;
using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.ScriptExecution;
using Serilog;

namespace Squid.Tentacle.Flavors.KubernetesAgent;

public sealed class KubernetesAgentFlavor : ITentacleFlavor
{
    public string Id => "KubernetesAgent";

    public TentacleFlavorRuntime CreateRuntime(TentacleFlavorContext context)
    {
        var tentacleSettings = context.TentacleSettings;

        var kubernetesSettings = new KubernetesSettings();
        context.Configuration.GetSection("Kubernetes").Bind(kubernetesSettings);

        var registrar = new KubernetesAgentRegistrar(tentacleSettings, kubernetesSettings);

        var backgroundTasks = new List<ITentacleBackgroundTask>();
        var startupHooks = new List<ITentacleStartupHook> { new InitializationFlagStartupHook() };
        var readinessChecks = new List<Func<bool>>();
        ITentacleScriptBackend backend;

        if (!kubernetesSettings.UseScriptPods)
        {
            Log.Information("Using local script execution mode");
            backend = new LocalScriptService();
        }
        else
        {
            Log.Information("Using Script Pod execution mode. Image={Image}", kubernetesSettings.ScriptPodImage);

            var k8sConfig = KubernetesClientConfiguration.IsInCluster()
                ? KubernetesClientConfiguration.InClusterConfig()
                : KubernetesClientConfiguration.BuildConfigFromConfigFile();

            var k8sClient = new k8s.Kubernetes(k8sConfig);
            IKubernetesPodOperations podOps = new ResilientKubernetesPodOperations(new KubernetesPodOperations(k8sClient));
            var podMgr = new KubernetesPodManager(podOps, kubernetesSettings);
            var scriptPodService = new ScriptPodService(tentacleSettings, kubernetesSettings, podMgr);
            var podMonitor = new KubernetesPodMonitor(podMgr, scriptPodService, tentacleSettings, kubernetesSettings);

            var recoveryService = new ScriptRecoveryService();
            recoveryService.RecoverScripts(tentacleSettings.WorkspacePath, scriptPodService, podMgr, scriptPodService.IsolationMutex);

            backend = scriptPodService;
            backgroundTasks.Add(new ResilientBackgroundTask(new KubernetesPodMonitorBackgroundTask(podMonitor)));
            backgroundTasks.Add(new ResilientBackgroundTask(new KubernetesEventMonitor(podOps, kubernetesSettings)));

            var podWatcher = new KubernetesPodWatcher(podOps, kubernetesSettings);
            backgroundTasks.Add(new ResilientBackgroundTask(new KubernetesPodWatcherBackgroundTask(podWatcher)));

            startupHooks.Add(new ClusterVersionDetector(k8sClient));

            var watchdogEnabled = string.Equals(Environment.GetEnvironmentVariable("WATCHDOG_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);

            if (!watchdogEnabled)
            {
                var nfsWatchdog = new NfsWatchdog(scriptPodService.WorkspaceBasePath);
                backgroundTasks.Add(new ResilientBackgroundTask(nfsWatchdog));
                readinessChecks.Add(() => nfsWatchdog.IsHealthy);
            }
            else
            {
                Log.Information("External watchdog sidecar is enabled, skipping in-process NFS watchdog");
            }
        }

        Func<bool> CombinedReadiness()
        {
            if (readinessChecks.Count == 0) return null;

            return () => readinessChecks.All(check => check());
        }

        return new TentacleFlavorRuntime
        {
            Registrar = registrar,
            ScriptBackend = backend,
            BackgroundTasks = backgroundTasks,
            StartupHooks = startupHooks,
            ReadinessCheck = CombinedReadiness()
        };
    }
}
