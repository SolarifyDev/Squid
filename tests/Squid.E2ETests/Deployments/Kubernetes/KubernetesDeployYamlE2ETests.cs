using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Kubernetes;
using Squid.E2ETests.Infrastructure;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Process;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes;

public class KubernetesDeployYamlE2ETests : KubernetesE2ETestBase
{
    private readonly KubernetesContextScriptBuilder _contextBuilder = new();
    private readonly KubernetesDeployYamlActionHandler _yamlHandler = new();

    public KubernetesDeployYamlE2ETests(KindClusterFixture cluster) : base(cluster)
    {
    }

    [Fact]
    public async Task DeployYaml_InlineConfigMap_Bash_AppliesSuccessfully()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-yaml-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            // Create namespace first
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var yaml = $@"apiVersion: v1
kind: ConfigMap
metadata:
  name: e2e-inline-test
  namespace: {testNs}
data:
  app-config: |
    debug=true
    port=8080";

            var action = new DeploymentActionDto
            {
                ActionType = "Squid.KubernetesDeployRawYaml",
                Properties = new List<DeploymentActionPropertyDto>
                {
                    new() { PropertyName = "Squid.Action.KubernetesYaml.InlineYaml", PropertyValue = yaml },
                    new() { PropertyName = "Squid.Action.Script.Syntax", PropertyValue = "Bash" }
                }
            };

            var ctx = new ActionExecutionContext { Action = action };
            var result = await _yamlHandler.PrepareAsync(ctx, CancellationToken.None);

            // Write files to temp dir
            var tempDir = Path.Combine(Path.GetTempPath(), $"squid-yaml-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            foreach (var file in result.Files)
            {
                await File.WriteAllBytesAsync(Path.Combine(tempDir, file.Key), file.Value);
            }

            var modifiedScript = $"cd \"{tempDir}\"\n{result.ScriptBody}";

            var endpoint = new KubernetesEndpointDto
            {
                ClusterUrl = clusterUrl,
                Namespace = testNs,
                SkipTlsVerification = "True"
            };
            var account = new DeploymentAccount
            {
                AccountType = AccountType.Token,
                Token = token
            };

            var fullScript = _contextBuilder.WrapWithContext(
                modifiedScript,
                endpoint,
                account,
                ScriptSyntax.Bash);

            var scriptResult = await ExecuteBashScriptAsync(fullScript);
            scriptResult.ExitCode.ShouldBe(0, $"Deploy YAML failed: {scriptResult.StdErr}");

            // Verify the ConfigMap was created
            var configMap = await Cluster.KubectlAsync($"-n {testNs} get configmap e2e-inline-test -o yaml");
            configMap.ShouldContain("debug=true");
            configMap.ShouldContain("port=8080");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployYaml_MultiDocumentYaml_Bash_AppliesAll()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-yaml-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var yaml = $@"apiVersion: v1
kind: ConfigMap
metadata:
  name: e2e-multi-1
  namespace: {testNs}
data:
  key1: value1
---
apiVersion: v1
kind: ConfigMap
metadata:
  name: e2e-multi-2
  namespace: {testNs}
data:
  key2: value2";

            var action = new DeploymentActionDto
            {
                ActionType = "Squid.KubernetesDeployRawYaml",
                Properties = new List<DeploymentActionPropertyDto>
                {
                    new() { PropertyName = "Squid.Action.KubernetesYaml.InlineYaml", PropertyValue = yaml },
                    new() { PropertyName = "Squid.Action.Script.Syntax", PropertyValue = "Bash" }
                }
            };

            var ctx = new ActionExecutionContext { Action = action };
            var result = await _yamlHandler.PrepareAsync(ctx, CancellationToken.None);

            var tempDir = Path.Combine(Path.GetTempPath(), $"squid-yaml-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            foreach (var file in result.Files)
            {
                await File.WriteAllBytesAsync(Path.Combine(tempDir, file.Key), file.Value);
            }

            var modifiedScript = $"cd \"{tempDir}\"\n{result.ScriptBody}";

            var endpoint = new KubernetesEndpointDto
            {
                ClusterUrl = clusterUrl,
                Namespace = testNs,
                SkipTlsVerification = "True"
            };
            var account = new DeploymentAccount
            {
                AccountType = AccountType.Token,
                Token = token
            };

            var fullScript = _contextBuilder.WrapWithContext(
                modifiedScript,
                endpoint,
                account,
                ScriptSyntax.Bash);

            var scriptResult = await ExecuteBashScriptAsync(fullScript);
            scriptResult.ExitCode.ShouldBe(0, $"Deploy multi-doc YAML failed: {scriptResult.StdErr}");

            // Verify both ConfigMaps were created
            var cm1 = await Cluster.KubectlAsync($"-n {testNs} get configmap e2e-multi-1 -o yaml");
            cm1.ShouldContain("value1");
            var cm2 = await Cluster.KubectlAsync($"-n {testNs} get configmap e2e-multi-2 -o yaml");
            cm2.ShouldContain("value2");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }
}
