using Squid.Tentacle.Tests.Support;

namespace Squid.Tentacle.Tests.Kubernetes.Chart;

[Trait("Category", TentacleTestCategories.Kubernetes)]
public class SquidTentacleHelmChartContractTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly string ChartValuesPath = Path.Combine(
        RepoRoot, "deploy", "helm", "squid-tentacle", "values.yaml");

    private static readonly string DeploymentTemplatePath = Path.Combine(
        RepoRoot, "deploy", "helm", "squid-tentacle", "templates", "deployment.yaml");

    [Fact]
    public void Values_DefaultWorkspaceAccessModes_IncludeReadWriteMany()
    {
        var values = File.ReadAllText(ChartValuesPath);

        values.ShouldContain("workspace:");
        values.ShouldContain("accessModes:");
        values.ShouldContain("ReadWriteMany");
    }

    [Fact]
    public void Values_Define_ScriptPod_Image_Default()
    {
        var values = File.ReadAllText(ChartValuesPath);

        values.ShouldContain("scriptPod:");
        values.ShouldContain("image:");
    }

    [Fact]
    public void DeploymentTemplate_Injects_Core_Tentacle_EnvVars()
    {
        var yaml = File.ReadAllText(DeploymentTemplatePath);

        yaml.ShouldContain("Tentacle__ServerUrl");
        yaml.ShouldContain("Tentacle__ServerPollingPort");
        yaml.ShouldContain("Tentacle__BearerToken");
        yaml.ShouldContain("Tentacle__Roles");
    }

    [Fact]
    public void DeploymentTemplate_Enables_ScriptPods_By_Default()
    {
        var yaml = File.ReadAllText(DeploymentTemplatePath);

        yaml.ShouldContain("- name: Kubernetes__UseScriptPods");
        yaml.ShouldContain("value: \"true\"");
    }

    [Fact]
    public void DeploymentTemplate_Does_Not_Require_Tentacle_Flavor_Env_For_Current_Default()
    {
        var yaml = File.ReadAllText(DeploymentTemplatePath);

        yaml.ShouldNotContain("Tentacle__Flavor");
    }
}
