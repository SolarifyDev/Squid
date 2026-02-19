using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Kubernetes;
using Squid.E2ETests.Infrastructure;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes;

public class KubernetesContextScriptE2ETests : KubernetesE2ETestBase
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

        var endpoint = new KubernetesApiEndpointDto
        {
            ClusterUrl = clusterUrl,
            Namespace = "default",
            SkipTlsVerification = "True"
        };

        var account = new DeploymentAccount
        {
            AccountType = AccountType.Token,
            Token = token
        };

        var script = _builder.WrapWithContext(
            "kubectl get pods --all-namespaces",
            endpoint,
            account,
            ScriptSyntax.Bash);

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

        var endpoint = new KubernetesApiEndpointDto
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

        // The context script auto-creates namespace if not "default"
        var script = _builder.WrapWithContext(
            $"kubectl get namespace {testNs}",
            endpoint,
            account,
            ScriptSyntax.Bash);

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

        var endpoint = new KubernetesApiEndpointDto
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
            endpoint,
            account,
            ScriptSyntax.Bash);

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

        var endpoint = new KubernetesApiEndpointDto
        {
            ClusterUrl = clusterUrl,
            Namespace = "default",
            SkipTlsVerification = "True"
        };

        var account = new DeploymentAccount
        {
            AccountType = AccountType.Token,
            Token = token
        };

        var script = _builder.WrapWithContext(
            "kubectl cluster-info",
            endpoint,
            account,
            ScriptSyntax.Bash);

        var result = await ExecuteBashScriptAsync(script);
        result.ExitCode.ShouldBe(0, $"Script failed: {result.StdErr}");
        result.StdOut.ShouldContain("Kubernetes");
    }
}
