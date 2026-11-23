using k8s;
using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Health;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.SelfHeal;
using Serilog;

namespace Squid.Tentacle.Flavors.KubernetesAgent;

public sealed class KubernetesAgentFlavor : ITentacleFlavor
{
    public string Id => "KubernetesAgent";

    private static Dictionary<string, string> BuildMetadata(KubernetesSettings settings, TentacleSettings tentacleSettings)
    {
        var metadata = new Dictionary<string, string>
        {
            ["flavor"] = "KubernetesAgent",
            ["scriptPodMode"] = settings.UseScriptPods ? "ScriptPod" : "Local",
            ["scriptPodImage"] = settings.ScriptPodImage,
            ["namespace"] = settings.TentacleNamespace,
            ["workspaceIsolation"] = settings.IsolateWorkspaceToEmptyDir ? "EmptyDir" : "SharedPVC",
            ["nfsWatchdogEnabled"] = (!string.IsNullOrEmpty(settings.NfsWatchdogImage)).ToString().ToLowerInvariant(),
            ["scriptPodCpuLimit"] = settings.ScriptPodCpuLimit,
            ["scriptPodMemoryLimit"] = settings.ScriptPodMemoryLimit
        };

        var usage = DiskSpaceChecker.GetWorkspaceUsage(tentacleSettings.WorkspacePath);

        if (usage.TotalBytes > 0)
        {
            metadata["workspaceTotalBytes"] = usage.TotalBytes.ToString();
            metadata["workspaceFreeBytes"] = usage.FreeBytes.ToString();
            metadata["workspaceUsedBytes"] = usage.UsedBytes.ToString();
        }

        return metadata;
    }

    /// <summary>
    /// Appends the workspace readiness checks in the order they must run: the NFS watchdog verdict (a flag read)
    /// FIRST, the disk check LAST. <c>All(...)</c> short-circuits, so a hung NFS mount that the watchdog already
    /// flagged is never statfs'd by the readiness probe.
    /// </summary>
    internal static void AddWorkspaceReadinessChecks(List<Func<bool>> readinessChecks, Func<bool> nfsHealthy, Func<bool> diskReady)
    {
        if (nfsHealthy != null) readinessChecks.Add(nfsHealthy);

        readinessChecks.Add(diskReady);
    }

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

            var localBackend = new LocalScriptService();
            backend = localBackend;

            // Local-exec mode (the default K8s-agent mode) creates the identical
            // {tempRoot}/squid-tentacle-{ticketId} workspaces the standalone Tentacle does, so a
            // disk-full agent must auto-reclaim here too — schedule the same disk-pressure
            // self-heal TentacleFlavor uses (the backend is its own live-script veto reporter).
            backgroundTasks.Add(SelfHealBackgroundTask.ForLocalWorkspaces(localBackend));
        }
        else
        {
            Log.Information("Using Script Pod execution mode. Image={Image}", kubernetesSettings.ScriptPodImage);

            var k8sConfig = KubernetesClientConfiguration.IsInCluster()
                ? KubernetesClientConfiguration.InClusterConfig()
                : KubernetesClientConfiguration.BuildConfigFromConfigFile();

            var k8sClient = new k8s.Kubernetes(k8sConfig);
            IKubernetesPodOperations podOps = new ResilientKubernetesPodOperations(new KubernetesPodOperations(k8sClient));
            var podStateCache = new PodStateCache();
            var templateProvider = new ScriptPodTemplateProvider(k8sClient, kubernetesSettings, podOps);
            var pullSecretManager = new ImagePullSecretManager(podOps, kubernetesSettings);
            var podMgr = new KubernetesPodManager(podOps, kubernetesSettings, templateProvider, pullSecretManager, podStateCache);
            var scriptPodService = new ScriptPodService(tentacleSettings, kubernetesSettings, podMgr, podOps);

            var pdbManager = new PodDisruptionBudgetManager(podOps, kubernetesSettings);
            pdbManager.EnsurePdbExists();

            var apiProbe = new KubernetesApiHealthProbe(podOps, kubernetesSettings);
            var podMonitorWithPdb = new KubernetesPodMonitor(podMgr, scriptPodService, tentacleSettings, kubernetesSettings, pdbManager, apiProbe);

            var recoveryService = new ScriptRecoveryService();
            recoveryService.RecoverScripts(tentacleSettings.WorkspacePath, scriptPodService, podMgr, scriptPodService.IsolationMutex);
            recoveryService.RecoverPendingScripts(podOps, kubernetesSettings, scriptPodService);

            backend = scriptPodService;
            backgroundTasks.Add(new ResilientBackgroundTask(new KubernetesPodMonitorBackgroundTask(podMonitorWithPdb)));
            backgroundTasks.Add(new ResilientBackgroundTask(new KubernetesEventMonitor(podOps, kubernetesSettings, scriptPodService)));

            var podWatcher = new KubernetesPodWatcher(podOps, kubernetesSettings, podStateCache);
            backgroundTasks.Add(new ResilientBackgroundTask(new KubernetesPodWatcherBackgroundTask(podWatcher)));

            startupHooks.Add(new ClusterVersionDetector(k8sClient));

            readinessChecks.Add(() => apiProbe.IsHealthy);

            var watchdogEnabled = string.Equals(Environment.GetEnvironmentVariable("WATCHDOG_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);
            Func<bool> nfsHealthy = null;

            if (!watchdogEnabled)
            {
                var nfsWatchdog = new NfsWatchdog(scriptPodService.WorkspaceBasePath, podOps, kubernetesSettings);
                backgroundTasks.Add(new ResilientBackgroundTask(nfsWatchdog));
                nfsHealthy = () => nfsWatchdog.IsHealthy;
            }
            else
            {
                Log.Information("External watchdog sidecar is enabled, skipping in-process NFS watchdog");
            }

            var diskProbe = new DiskSpaceReadinessProbe(tentacleSettings.WorkspacePath, tentacleSettings.DiskSpaceReadinessTimeoutSeconds);
            AddWorkspaceReadinessChecks(readinessChecks, nfsHealthy, diskProbe.IsReady);
        }

        Func<bool> CombinedReadiness()
        {
            if (readinessChecks.Count == 0) return null;

            return () => readinessChecks.All(check => check());
        }

        var metadata = BuildMetadata(kubernetesSettings, tentacleSettings);

        return new TentacleFlavorRuntime
        {
            Registrar = registrar,
            ScriptBackend = backend,
            BackgroundTasks = backgroundTasks,
            StartupHooks = startupHooks,
            ReadinessCheck = CombinedReadiness(),
            Metadata = metadata
        };
    }
}
