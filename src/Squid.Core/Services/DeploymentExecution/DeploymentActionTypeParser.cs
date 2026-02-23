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

        return false;
    }

    public static bool Is(string actionType, DeploymentActionType expected)
        => TryParse(actionType, out var parsed) && parsed == expected;
}
