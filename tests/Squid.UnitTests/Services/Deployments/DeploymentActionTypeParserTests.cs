using Squid.Core.Services.DeploymentExecution;

namespace Squid.UnitTests.Services.Deployments;

public class DeploymentActionTypeParserTests
{
    [Theory]
    [InlineData("Squid.KubernetesRunScript", DeploymentActionType.KubernetesRunScript)]
    [InlineData("Squid.KubernetesDeployRawYaml", DeploymentActionType.KubernetesDeployRawYaml)]
    [InlineData("Squid.KubernetesDeployContainers", DeploymentActionType.KubernetesDeployContainers)]
    [InlineData("Squid.HelmChartUpgrade", DeploymentActionType.HelmChartUpgrade)]
    [InlineData("Squid.KubernetesDeployIngress", DeploymentActionType.KubernetesDeployIngress)]
    [InlineData("Squid.KubernetesDeployService", DeploymentActionType.KubernetesDeployService)]
    [InlineData("Squid.KubernetesDeployConfigMap", DeploymentActionType.KubernetesDeployConfigMap)]
    [InlineData("Squid.KubernetesDeploySecret", DeploymentActionType.KubernetesDeploySecret)]
    [InlineData("Squid.Manual", DeploymentActionType.ManualIntervention)]
    [InlineData("squid.manual", DeploymentActionType.ManualIntervention)]
    [InlineData("squid.kubernetesrunscript", DeploymentActionType.KubernetesRunScript)]
    [InlineData("SQUID.KUBERNETESDEPLOYSERVICE", DeploymentActionType.KubernetesDeployService)]
    public void TryParse_KnownActionType_ReturnsEnum(string input, DeploymentActionType expected)
    {
        var ok = DeploymentActionTypeParser.TryParse(input, out var parsed);

        ok.ShouldBeTrue();
        parsed.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Squid.UnknownAction")]
    public void TryParse_UnknownOrEmpty_ReturnsFalse(string input)
    {
        var ok = DeploymentActionTypeParser.TryParse(input, out _);

        ok.ShouldBeFalse();
    }
}
