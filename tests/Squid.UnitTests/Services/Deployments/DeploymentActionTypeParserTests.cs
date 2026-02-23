using Squid.Core.Services.DeploymentExecution;

namespace Squid.UnitTests.Services.Deployments;

public class DeploymentActionTypeParserTests
{
    [Theory]
    [InlineData("Squid.KubernetesRunScript", DeploymentActionType.KubernetesRunScript)]
    [InlineData("Squid.KubernetesDeployRawYaml", DeploymentActionType.KubernetesDeployRawYaml)]
    [InlineData("Squid.KubernetesDeployContainers", DeploymentActionType.KubernetesDeployContainers)]
    [InlineData("Squid.HelmChartUpgrade", DeploymentActionType.HelmChartUpgrade)]
    [InlineData("squid.kubernetesrunscript", DeploymentActionType.KubernetesRunScript)]
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
