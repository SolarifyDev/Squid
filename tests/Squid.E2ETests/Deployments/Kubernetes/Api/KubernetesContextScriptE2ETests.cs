using System.Text.Json;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.E2ETests.Infrastructure;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Shouldly;
using Xunit;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.E2ETests.Deployments.Kubernetes.Api;

public class KubernetesContextScriptE2ETests : KubernetesApiE2ETestBase
{
    private readonly KubernetesApiContextScriptBuilder _builder = new();

    public KubernetesContextScriptE2ETests(KindClusterFixture cluster) : base(cluster)
    {
    }

    [Fact]
    public async Task RunScript_GetPods_Bash_Succeeds()
    {
        // Extract cluster URL and token from kind cluster
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();

        var scriptContext = MakeScriptContext(clusterUrl, token, "default");

        var script = _builder.WrapWithContext(
            "kubectl get pods --all-namespaces",
            scriptContext);

        var result = await ExecuteBashScriptAsync(script);

        result.ExitCode.ShouldBe(0, $"Script failed with error: {result.StdErr}");
        result.StdOut.ShouldContain("kube-system");
    }

    [Fact]
    public async Task RunScript_CreateAndDeleteNamespace_Bash_Succeeds()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-e2e-{Guid.NewGuid().ToString("N")[..8]}";

        var scriptContext = MakeScriptContext(clusterUrl, token, testNs);

        // The context script auto-creates namespace if not "default"
        var script = _builder.WrapWithContext(
            $"kubectl get namespace {testNs}",
            scriptContext);

        var result = await ExecuteBashScriptAsync(script);
        result.ExitCode.ShouldBe(0, $"Script failed: {result.StdErr}");
        result.StdOut.ShouldContain(testNs);

        // Cleanup
        await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
    }

    [Fact]
    public async Task RunScript_ApplyDeployment_Bash_Succeeds()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-e2e-{Guid.NewGuid().ToString("N")[..8]}";

        var scriptContext = MakeScriptContext(clusterUrl, token, testNs);

        var userScript = @"
cat <<'YAML' | kubectl apply -f -
apiVersion: v1
kind: ConfigMap
metadata:
  name: squid-e2e-test
  namespace: " + testNs + @"
data:
  test-key: test-value
YAML
kubectl get configmap squid-e2e-test -n " + testNs;

        var script = _builder.WrapWithContext(
            userScript,
            scriptContext);

        var result = await ExecuteBashScriptAsync(script);
        result.ExitCode.ShouldBe(0, $"Script failed: {result.StdErr}");
        result.StdOut.ShouldContain("squid-e2e-test");

        // Cleanup
        await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
    }

    [Fact]
    public async Task RunScript_SkipTlsVerification_Bash_Succeeds()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();

        var scriptContext = MakeScriptContext(clusterUrl, token, "default");

        var script = _builder.WrapWithContext(
            "kubectl cluster-info",
            scriptContext);

        var result = await ExecuteBashScriptAsync(script);
        result.ExitCode.ShouldBe(0, $"Script failed: {result.StdErr}");
        result.StdOut.ShouldContain("Kubernetes");
    }

    private static ScriptContext MakeScriptContext(
        string clusterUrl, string token, string ns,
        ScriptSyntax syntax = ScriptSyntax.Bash)
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
            Syntax = syntax
        };
    }
}
