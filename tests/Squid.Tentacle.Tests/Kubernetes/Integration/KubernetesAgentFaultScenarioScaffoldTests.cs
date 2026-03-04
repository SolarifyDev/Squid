using Squid.Tentacle.Tests.Kubernetes.Integration.Support;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Environment;
using Squid.Tentacle.Tests.Support.Paths;

namespace Squid.Tentacle.Tests.Kubernetes.Integration;

[Trait("Category", TentacleTestCategories.Kubernetes)]
[Trait("Category", TentacleTestCategories.Integration)]
public class KubernetesAgentFaultScenarioScaffoldTests : KubernetesAgentIntegrationTestBase
{
    [Fact]
    public async Task TentaclePodRestart_Smoke_Uses_Real_Kubectl_When_FaultScenarios_Are_Enabled()
    {
        var prereqs = KubernetesIntegrationPrerequisites.Detect();
        var settings = KubernetesE2EEnvironmentSettings.Load();

        settings.GetTentacleDeploymentName().ShouldNotBeNullOrWhiteSpace();
        settings.GetTentaclePodLabelSelector().ShouldContain("app.kubernetes.io/instance=");

        if (!settings.CanRunFaultScenarioSmoke(prereqs))
            return;

        var kubectl = new KubectlClient(WorkspacePaths.RepositoryRoot);
        var selector = settings.GetTentaclePodLabelSelector();
        var deploymentName = settings.GetTentacleDeploymentName();

        var before = await kubectl.GetPodsBySelectorAsync(settings.Namespace, selector, TestCancellationToken);
        before.ExitCode.ShouldBe(0, $"kubectl get pods failed:{Environment.NewLine}{before.StdOut}{Environment.NewLine}{before.StdErr}");

        var podName = ParseFirstPodName(before.StdOut);
        if (string.IsNullOrWhiteSpace(podName))
            return;

        var delete = await kubectl.DeletePodAsync(settings.Namespace, podName, wait: true, TestCancellationToken);
        delete.ExitCode.ShouldBe(0, $"kubectl delete pod failed:{Environment.NewLine}{delete.StdOut}{Environment.NewLine}{delete.StdErr}");

        var rollout = await kubectl.RolloutStatusDeploymentAsync(settings.Namespace, deploymentName, TimeSpan.FromMinutes(5), TestCancellationToken);
        rollout.ExitCode.ShouldBe(0, $"kubectl rollout status failed:{Environment.NewLine}{rollout.StdOut}{Environment.NewLine}{rollout.StdErr}");

        var after = await kubectl.GetPodsBySelectorAsync(settings.Namespace, selector, TestCancellationToken);
        after.ExitCode.ShouldBe(0);
        ParsePodNames(after.StdOut).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ScriptPodDelete_Smoke_Uses_Real_Kubectl_When_LabelSelector_Is_Configured()
    {
        var prereqs = KubernetesIntegrationPrerequisites.Detect();
        var settings = KubernetesE2EEnvironmentSettings.Load();

        // Script pod selector is intentionally explicit because script pods only exist while a deployment script is running.
        // Example: squid.io/ticket-id=<ticket>
        settings.ScriptPodLabelSelector.ShouldNotBeNull();

        if (!settings.CanRunFaultScenarioSmoke(prereqs))
            return;

        if (string.IsNullOrWhiteSpace(settings.ScriptPodLabelSelector))
            return;

        var kubectl = new KubectlClient(WorkspacePaths.RepositoryRoot);

        var listed = await kubectl.GetPodsBySelectorAsync(settings.Namespace, settings.ScriptPodLabelSelector, TestCancellationToken);
        listed.ExitCode.ShouldBe(0, $"kubectl get script pods failed:{Environment.NewLine}{listed.StdOut}{Environment.NewLine}{listed.StdErr}");

        var podName = ParseFirstPodName(listed.StdOut);
        if (string.IsNullOrWhiteSpace(podName))
            return;

        var delete = await kubectl.DeletePodAsync(settings.Namespace, podName, wait: true, TestCancellationToken);
        delete.ExitCode.ShouldBe(0, $"kubectl delete script pod failed:{Environment.NewLine}{delete.StdOut}{Environment.NewLine}{delete.StdErr}");
    }

    private static List<string> ParsePodNames(string stdout)
    {
        return stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.StartsWith("pod/", StringComparison.OrdinalIgnoreCase) ? line["pod/".Length..] : line)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private static string ParseFirstPodName(string stdout)
        => ParsePodNames(stdout).FirstOrDefault() ?? string.Empty;
}
