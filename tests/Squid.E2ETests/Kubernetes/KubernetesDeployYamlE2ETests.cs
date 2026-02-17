using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Kubernetes;
using Squid.E2ETests.Infrastructure;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Process;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Kubernetes;

/// <summary>
/// E2E tests for kubectl apply YAML operations against a real kind cluster.
/// </summary>
[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class KubernetesDeployYamlE2ETests
{
    private readonly KindClusterFixture _cluster;
    private readonly KubernetesContextScriptBuilder _contextBuilder = new();
    private readonly KubernetesDeployYamlActionHandler _yamlHandler = new();

    public KubernetesDeployYamlE2ETests(KindClusterFixture cluster)
    {
        _cluster = cluster;
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
            await _cluster.KubectlAsync($"create namespace {testNs}");

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
            var configMap = await _cluster.KubectlAsync($"-n {testNs} get configmap e2e-inline-test -o yaml");
            configMap.ShouldContain("debug=true");
            configMap.ShouldContain("port=8080");
        }
        finally
        {
            await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
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
            await _cluster.KubectlAsync($"create namespace {testNs}");

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
            var cm1 = await _cluster.KubectlAsync($"-n {testNs} get configmap e2e-multi-1 -o yaml");
            cm1.ShouldContain("value1");
            var cm2 = await _cluster.KubectlAsync($"-n {testNs} get configmap e2e-multi-2 -o yaml");
            cm2.ShouldContain("value2");
        }
        finally
        {
            await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    // === Helper Methods ===

    private async Task<string> GetClusterUrlAsync()
    {
        var output = await _cluster.KubectlAsync("config view --minify -o jsonpath='{.clusters[0].cluster.server}'");
        return output.Trim('\'');
    }

    private async Task<string> GetServiceAccountTokenAsync()
    {
        const string sa = "squid-e2e-admin";
        const string ns = "kube-system";
        try { await _cluster.KubectlAsync($"create serviceaccount {sa} -n {ns}"); } catch { }
        try { await _cluster.KubectlAsync($"create clusterrolebinding {sa}-binding --clusterrole=cluster-admin --serviceaccount={ns}:{sa}"); } catch { }
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
