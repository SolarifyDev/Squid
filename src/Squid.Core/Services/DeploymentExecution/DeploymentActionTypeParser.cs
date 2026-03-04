namespace Squid.Core.Services.DeploymentExecution;

public static class DeploymentActionTypeParser
{
    public static bool TryParse(string actionType, out DeploymentActionType parsed)
    {
        parsed = default;

        if (string.IsNullOrWhiteSpace(actionType))
            return false;

        if (string.Equals(actionType, "Squid.KubernetesRunScript", StringComparison.OrdinalIgnoreCase))
        {
            parsed = DeploymentActionType.KubernetesRunScript;
            return true;
        }

        if (string.Equals(actionType, "Squid.KubernetesDeployRawYaml", StringComparison.OrdinalIgnoreCase))
        {
            parsed = DeploymentActionType.KubernetesDeployRawYaml;
            return true;
        }

        if (string.Equals(actionType, "Squid.KubernetesDeployContainers", StringComparison.OrdinalIgnoreCase))
        {
            parsed = DeploymentActionType.KubernetesDeployContainers;
            return true;
        }

        if (string.Equals(actionType, "Squid.HelmChartUpgrade", StringComparison.OrdinalIgnoreCase))
        {
            parsed = DeploymentActionType.HelmChartUpgrade;
            return true;
        }

        if (string.Equals(actionType, "Squid.KubernetesDeployIngress", StringComparison.OrdinalIgnoreCase))
        {
            parsed = DeploymentActionType.KubernetesDeployIngress;
            return true;
        }

        if (string.Equals(actionType, "Squid.KubernetesDeployService", StringComparison.OrdinalIgnoreCase))
        {
            parsed = DeploymentActionType.KubernetesDeployService;
            return true;
        }

        if (string.Equals(actionType, "Squid.KubernetesDeployConfigMap", StringComparison.OrdinalIgnoreCase))
        {
            parsed = DeploymentActionType.KubernetesDeployConfigMap;
            return true;
        }

        if (string.Equals(actionType, "Squid.KubernetesDeploySecret", StringComparison.OrdinalIgnoreCase))
        {
            parsed = DeploymentActionType.KubernetesDeploySecret;
            return true;
        }

        return false;
    }

    public static bool Is(string actionType, DeploymentActionType expected)
        => TryParse(actionType, out var parsed) && parsed == expected;
}
