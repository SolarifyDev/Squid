using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Environment;
using Squid.Tentacle.Tests.Support.Process;

namespace Squid.Tentacle.Tests.Kubernetes.Chart;

[Trait("Category", TentacleTestCategories.Kubernetes)]
[Trait("Category", TentacleTestCategories.Integration)]
public class SquidTentacleHelmTemplateSmokeTests : TimedTestBase
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public async Task HelmTemplate_LocalChart_Renders_Expected_Resources_When_Helm_Is_Available()
    {
        if (!ExternalToolProbe.HasHelm())
            return;

        var chartPath = Path.Combine(RepoRoot, "deploy", "helm", "squid-tentacle");
        var result = await CommandRunner.RunAsync(
            "helm",
            $"template squid-tentacle \"{chartPath}\"",
            RepoRoot,
            TestCancellationToken);

        result.ExitCode.ShouldBe(0, result.StdErr);
        result.StdOut.ShouldContain("kind: Deployment");
        result.StdOut.ShouldContain("name: Tentacle__ServerUrl");
        result.StdOut.ShouldContain("name: Kubernetes__UseScriptPods");
    }
}
