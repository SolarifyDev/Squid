using System;
using System.IO;
using System.Threading.Tasks;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Kubernetes;
using Squid.E2ETests.Infrastructure;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Kubernetes;

/// <summary>
/// E2E tests that validate generated kubectl context scripts execute correctly
/// against a real kind cluster.
///
/// Run: SQUID_KEEP_CLUSTER=true dotnet test --filter "Category=E2E"
/// Requires: Docker + kind + kubectl
/// </summary>
[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class KubernetesContextScriptE2ETests
{
    private readonly KindClusterFixture _cluster;
    private readonly KubernetesContextScriptBuilder _builder = new();

    public KubernetesContextScriptE2ETests(KindClusterFixture cluster)
    {
        _cluster = cluster;
    }

    [Fact]
    public async Task RunScript_GetPods_Bash_Succeeds()
    {
        // Extract cluster URL and token from kind cluster
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();

        var endpoint = new KubernetesEndpointDto
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
        await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
    }

    [Fact]
    public async Task RunScript_ApplyDeployment_Bash_Succeeds()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-e2e-{Guid.NewGuid().ToString("N")[..8]}";

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
        await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
    }

    [Fact]
    public async Task RunScript_SkipTlsVerification_Bash_Succeeds()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();

        var endpoint = new KubernetesEndpointDto
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

    // === Helper Methods ===

    private async Task<string> GetClusterUrlAsync()
    {
        var output = await _cluster.KubectlAsync("config view --minify -o jsonpath='{.clusters[0].cluster.server}'");
        return output.Trim('\'');
    }

    private async Task<string> GetServiceAccountTokenAsync()
    {
        // Create a service account with cluster-admin for testing
        const string sa = "squid-e2e-admin";
        const string ns = "kube-system";

        try
        {
            await _cluster.KubectlAsync($"create serviceaccount {sa} -n {ns}");
        }
        catch
        {
            // Already exists
        }

        try
        {
            await _cluster.KubectlAsync(
                $"create clusterrolebinding {sa}-binding --clusterrole=cluster-admin --serviceaccount={ns}:{sa}");
        }
        catch
        {
            // Already exists
        }

        // Create a token for the service account
        var token = await _cluster.KubectlAsync($"create token {sa} -n {ns} --duration=3600s");
        return token.Trim();
    }

    private static async Task<ScriptResult> ExecuteBashScriptAsync(string script)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"squid-e2e-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(scriptPath, script);

        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "bash",
                    Arguments = scriptPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return new ScriptResult(process.ExitCode, stdout, stderr);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    private record ScriptResult(int ExitCode, string StdOut, string StdErr);
}
