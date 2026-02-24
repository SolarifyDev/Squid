using Squid.Tentacle.Tests.Kubernetes.Integration.Support;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Environment;
using Squid.Tentacle.Tests.Support.Paths;

namespace Squid.Tentacle.Tests.Kubernetes.Integration;

[Trait("Category", TentacleTestCategories.Kubernetes)]
[Trait("Category", TentacleTestCategories.Integration)]
public class KubernetesAgentHelmInstallSmokeTests : KubernetesAgentIntegrationTestBase
{
    [Fact]
    public async Task HelmInstallSmoke_Can_Render_And_Optionally_Install_With_Real_Cluster_Config()
    {
        var prereqs = KubernetesIntegrationPrerequisites.Detect();
        var settings = KubernetesE2EEnvironmentSettings.Load();

        // Always assert preflight metadata so this test is not a pure no-op when external prereqs are missing.
        settings.ReleaseName.ShouldNotBeNullOrWhiteSpace();
        settings.Namespace.ShouldNotBeNullOrWhiteSpace();

        if (!settings.Enabled || !prereqs.IsAvailable || !settings.HasRequiredInstallSettings)
        {
            return;
        }

        var repoRoot = WorkspacePaths.RepositoryRoot;
        var chartPath = Path.Combine(repoRoot, "deploy", "helm", "squid-tentacle");
        Directory.Exists(chartPath).ShouldBeTrue();

        using var tempDir = new TemporaryDirectory();
        var valuesPath = Path.Combine(tempDir.Path, "values.override.yaml");
        var valuesYaml = SquidTentacleHelmValuesOverrideBuilder.BuildYaml(new SquidTentacleHelmValuesOverride
        {
            TentacleImageRepository = settings.TentacleImageRepository,
            TentacleImageTag = settings.TentacleImageTag,
            ScriptPodImage = settings.ScriptPodImage,
            ServerUrl = settings.ServerUrl,
            BearerToken = settings.BearerToken,
            KubernetesNamespace = settings.KubernetesTargetNamespace,
            WorkspaceStorageClassName = settings.WorkspaceStorageClassName
        });
        await File.WriteAllTextAsync(valuesPath, valuesYaml, TestCancellationToken);

        var helm = new HelmClient(repoRoot);
        var kubectl = new KubectlClient(repoRoot);
        var kind = new KindClient(repoRoot);

        var createdKindCluster = false;
        try
        {
            if (settings.CreateKindCluster)
            {
                var create = await kind.CreateClusterAsync(settings.ClusterName, TestCancellationToken);
                create.ExitCode.ShouldBe(0, $"kind create cluster failed:{Environment.NewLine}{create.StdOut}{Environment.NewLine}{create.StdErr}");
                createdKindCluster = true;
            }

            var render = await helm.TemplateAsync(settings.ReleaseName, chartPath, settings.Namespace, valuesPath, TestCancellationToken);
            render.ExitCode.ShouldBe(0, $"helm template failed:{Environment.NewLine}{render.StdOut}{Environment.NewLine}{render.StdErr}");
            render.StdOut.ShouldContain("Tentacle__ServerUrl");
            render.StdOut.ShouldContain(settings.ServerUrl);

            var install = await helm.UpgradeInstallAsync(settings.ReleaseName, chartPath, settings.Namespace, valuesPath, TestCancellationToken);
            install.ExitCode.ShouldBe(0, $"helm upgrade --install failed:{Environment.NewLine}{install.StdOut}{Environment.NewLine}{install.StdErr}");

            var deployments = await kubectl.GetDeploymentsAsync(settings.Namespace, TestCancellationToken);
            deployments.ExitCode.ShouldBe(0, $"kubectl get deployments failed:{Environment.NewLine}{deployments.StdOut}{Environment.NewLine}{deployments.StdErr}");
            deployments.StdOut.ShouldContain(settings.ReleaseName);
        }
        finally
        {
            if (settings.Enabled && prereqs.HasHelm)
            {
                _ = await helm.UninstallAsync(settings.ReleaseName, settings.Namespace, CancellationToken.None);
            }

            if (createdKindCluster && settings.CleanupKindCluster)
            {
                _ = await kind.DeleteClusterAsync(settings.ClusterName, CancellationToken.None);
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "squid-tentacle-k8s-e2e", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
