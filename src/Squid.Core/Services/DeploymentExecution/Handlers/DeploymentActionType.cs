namespace Squid.Core.Services.DeploymentExecution.Handlers;

public enum DeploymentActionType
{
    KubernetesRunScript = 1,
    KubernetesDeployRawYaml = 2,
    KubernetesDeployContainers = 3,
    HelmChartUpgrade = 4,
    KubernetesDeployIngress = 5,
    KubernetesDeployService = 6,
    KubernetesDeployConfigMap = 7,
    KubernetesDeploySecret = 8,
    ManualIntervention = 9,
    HealthCheck = 10,
}
