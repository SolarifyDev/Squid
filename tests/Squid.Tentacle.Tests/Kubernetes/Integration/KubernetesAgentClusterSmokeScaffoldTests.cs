using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Environment;
using Squid.Tentacle.Tests.Support.Paths;
using Squid.Tentacle.Tests.Support.Process;
using Squid.Tentacle.Tests.Support.Scenarios;

namespace Squid.Tentacle.Tests.Kubernetes.Integration;

[Trait("Category", TentacleTestCategories.Kubernetes)]
[Trait("Category", TentacleTestCategories.Integration)]
public class KubernetesAgentClusterSmokeScaffoldTests : KubernetesAgentIntegrationTestBase
{
    [Theory]
    [TentacleScenarioData(TentacleScenarioSet.KubernetesAgentCluster)]
    public async Task ClusterScenario_HelmTemplate_Smoke_Is_Runnable_When_Tools_Available(TentacleScenarioCase scenario)
    {
        scenario.RequiresKubernetesCluster.ShouldBeTrue();
        scenario.RequiresHelm.ShouldBeTrue();

        var prereqs = KubernetesIntegrationPrerequisites.Detect();
        if (!prereqs.IsAvailable)
        {
            return;
        }

        var chartPath = Path.Combine(WorkspacePaths.RepositoryRoot, "deploy", "helm", "kubernetes-agent");
        Directory.Exists(chartPath).ShouldBeTrue();

        var result = await CommandRunner.RunAsync(
            "helm",
            $"template kubernetes-agent \"{chartPath}\"",
            WorkspacePaths.RepositoryRoot,
            TestCancellationToken);

        result.ExitCode.ShouldBe(0, $"helm template failed. stdout:{Environment.NewLine}{result.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{result.StdErr}");
        result.StdOut.ShouldContain("kind: Deployment");
        result.StdOut.ShouldContain("Tentacle__ServerUrl");
    }
}
