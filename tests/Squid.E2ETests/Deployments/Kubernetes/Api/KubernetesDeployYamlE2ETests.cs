using System.Text.Json;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.E2ETests.Infrastructure;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Process;
using Shouldly;
using Xunit;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.E2ETests.Deployments.Kubernetes.Api;

public class KubernetesDeployYamlE2ETests : KubernetesApiE2ETestBase
{
    private readonly KubernetesApiContextScriptBuilder _contextBuilder = new();
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
            var intent = (KubernetesApplyIntent)await ((IActionHandler)_yamlHandler).DescribeIntentAsync(ctx, CancellationToken.None);

            // Write files to temp dir
            var tempDir = Path.Combine(Path.GetTempPath(), $"squid-yaml-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            foreach (var file in intent.YamlFiles)
            {
                await File.WriteAllBytesAsync(Path.Combine(tempDir, file.RelativePath), file.Content);
            }

            var modifiedScript = $"cd \"{tempDir}\"\nkubectl apply -f .";
            var scriptContext = MakeScriptContext(clusterUrl, token, testNs);

            var fullScript = _contextBuilder.WrapWithContext(modifiedScript, scriptContext);

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
            var intent = (KubernetesApplyIntent)await ((IActionHandler)_yamlHandler).DescribeIntentAsync(ctx, CancellationToken.None);

            var tempDir = Path.Combine(Path.GetTempPath(), $"squid-yaml-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            foreach (var file in intent.YamlFiles)
            {
                await File.WriteAllBytesAsync(Path.Combine(tempDir, file.RelativePath), file.Content);
            }

            var modifiedScript = $"cd \"{tempDir}\"\nkubectl apply -f .";
            var scriptContext = MakeScriptContext(clusterUrl, token, testNs);

            var fullScript = _contextBuilder.WrapWithContext(modifiedScript, scriptContext);

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

    private static ScriptContext MakeScriptContext(
        string clusterUrl, string token, string ns)
    {
        var endpoint = new EndpointContext
        {
            EndpointJson = JsonSerializer.Serialize(new KubernetesApiEndpointDto
            {
                ClusterUrl = clusterUrl,
                Namespace = ns,
                SkipTlsVerification = "True"
            })
        };
        endpoint.SetAccountData(AccountType.Token, DeploymentAccountCredentialsConverter.Serialize(
            new TokenCredentials { Token = token }));

        return new ScriptContext
        {
            Endpoint = endpoint,
            Syntax = ScriptSyntax.Bash
        };
    }
}
