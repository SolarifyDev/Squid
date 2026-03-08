using Squid.Calamari.Kubernetes;
using Squid.Calamari.Variables;
using Squid.E2ETests.Infrastructure;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Calamari.Kubernetes;

[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class KubernetesDeployIdAnnotationE2ETests
{
    private readonly KindClusterFixture _cluster;

    public KubernetesDeployIdAnnotationE2ETests(KindClusterFixture cluster)
    {
        _cluster = cluster;
    }

    [Theory]
    [InlineData("51")]
    [InlineData("999")]
    [InlineData("0")]
    public async Task DeployIdAnnotation_NumericValue_KubectlApplySucceeds(string deployId)
    {
        var testNs = $"squid-deployid-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");

            var renderedYaml = RenderDeploymentWithDeployId(testNs, deployId);

            var tempFile = Path.Combine(Path.GetTempPath(), $"squid-e2e-deployid-{Guid.NewGuid():N}.yaml");
            await File.WriteAllTextAsync(tempFile, renderedYaml);

            try
            {
                var result = await _cluster.KubectlAsync($"apply -f {tempFile}");
                result.ShouldContain("created");
            }
            finally
            {
                File.Delete(tempFile);
            }

            var annotations = await _cluster.KubectlAsync(
                $"-n {testNs} get deployment e2e-deploy-id-test -o jsonpath='{{.spec.template.metadata.annotations}}'");

            annotations.ShouldContain("squid.io/deploy-id");
            annotations.ShouldContain(deployId);
        }
        finally
        {
            await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployIdAnnotation_Redeploy_DifferentId_KubectlApplySucceeds()
    {
        var testNs = $"squid-deployid-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");

            var firstYaml = RenderDeploymentWithDeployId(testNs, "100");
            var tempFile = Path.Combine(Path.GetTempPath(), $"squid-e2e-deployid-{Guid.NewGuid():N}.yaml");
            await File.WriteAllTextAsync(tempFile, firstYaml);

            try
            {
                await _cluster.KubectlAsync($"apply -f {tempFile}");

                var secondYaml = RenderDeploymentWithDeployId(testNs, "101");
                await File.WriteAllTextAsync(tempFile, secondYaml);
                var result = await _cluster.KubectlAsync($"apply -f {tempFile}");

                result.ShouldContain("configured");
            }
            finally
            {
                File.Delete(tempFile);
            }

            var annotations = await _cluster.KubectlAsync(
                $"-n {testNs} get deployment e2e-deploy-id-test -o jsonpath='{{.spec.template.metadata.annotations}}'");

            annotations.ShouldContain("101");
            annotations.ShouldNotContain("100");
        }
        finally
        {
            await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployIdAnnotation_NonWorkloadKind_NoAnnotationInjected()
    {
        var testNs = $"squid-deployid-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");

            var renderedYaml = RenderConfigMapWithDeployId(testNs, "42");

            var tempFile = Path.Combine(Path.GetTempPath(), $"squid-e2e-deployid-{Guid.NewGuid():N}.yaml");
            await File.WriteAllTextAsync(tempFile, renderedYaml);

            try
            {
                var result = await _cluster.KubectlAsync($"apply -f {tempFile}");
                result.ShouldContain("created");
            }
            finally
            {
                File.Delete(tempFile);
            }

            var cm = await _cluster.KubectlAsync($"-n {testNs} get configmap e2e-no-inject -o yaml");
            cm.ShouldNotContain("squid.io/deploy-id");
        }
        finally
        {
            await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    private static string RenderDeploymentWithDeployId(string ns, string deployId)
    {
        var yaml = $"""
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: e2e-deploy-id-test
              namespace: {ns}
            spec:
              replicas: 1
              selector:
                matchLabels:
                  app: e2e-deploy-id-test
              strategy:
                type: RollingUpdate
              template:
                metadata:
                  labels:
                    app: e2e-deploy-id-test
                spec:
                  containers:
                  - name: nginx
                    image: nginx:latest
            """;

        return RenderThroughCalamariRenderer(yaml, deployId);
    }

    private static string RenderConfigMapWithDeployId(string ns, string deployId)
    {
        var yaml = $"""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: e2e-no-inject
              namespace: {ns}
            data:
              key: value
            """;

        return RenderThroughCalamariRenderer(yaml, deployId);
    }

    private static string RenderThroughCalamariRenderer(string yaml, string deployId)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"squid-e2e-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var inputPath = Path.Combine(tempDir, "manifest.yaml");
        File.WriteAllText(inputPath, yaml);

        var variables = new VariableSet();
        variables.Set("Squid.Deployment.Id", deployId);

        var renderer = new TokenSubstitutingYamlManifestRenderer();
        var rendered = renderer.RenderAsync(
            new KubernetesApplyRequest
            {
                WorkingDirectory = tempDir,
                YamlFilePath = inputPath,
                Variables = variables,
                TemporaryFiles = new List<string>()
            },
            new ResolvedKubernetesManifestSource
            {
                ManifestRootDirectory = tempDir,
                ManifestFilePaths = [inputPath]
            },
            CancellationToken.None).GetAwaiter().GetResult();

        return File.ReadAllText(rendered.ApplyPath);
    }
}
