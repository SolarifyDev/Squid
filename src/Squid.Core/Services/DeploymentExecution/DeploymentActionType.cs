namespace Squid.Core.Services.DeploymentExecution;

public enum DeploymentActionType
{
    KubernetesRunScript = 1,
    KubernetesDeployRawYaml = 2,
    KubernetesDeployContainers = 3,
    HelmChartUpgrade = 4,
    KubernetesDeployIngress = 5
}
