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
            var podOps = new KubernetesPodOperations(k8sClient);
            var podMgr = new KubernetesPodManager(podOps, kubernetesSettings);
            var scriptPodService = new ScriptPodService(tentacleSettings, kubernetesSettings, podMgr);
            var podMonitor = new KubernetesPodMonitor(podMgr, scriptPodService, tentacleSettings);

            backend = scriptPodService;
            backgroundTasks.Add(new KubernetesPodMonitorBackgroundTask(podMonitor));
        }

        return new TentacleFlavorRuntime
        {
            Registrar = registrar,
            ScriptBackend = backend,
            BackgroundTasks = backgroundTasks,
            StartupHooks = [new InitializationFlagStartupHook()]
        };
    }
}
