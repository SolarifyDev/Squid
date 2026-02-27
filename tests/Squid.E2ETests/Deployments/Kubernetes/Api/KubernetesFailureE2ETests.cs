using System.Text.Json;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.E2ETests.Infrastructure;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Process;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes.Api;

public class KubernetesFailureE2ETests : KubernetesApiE2ETestBase
{
    private readonly KubernetesApiContextScriptBuilder _contextBuilder = new();
    private readonly KubernetesDeployYamlActionHandler _yamlHandler = new();
    private readonly HelmUpgradeActionHandler _helmHandler = new();

    public KubernetesFailureE2ETests(KindClusterFixture cluster) : base(cluster)
    {
    }

    [Fact]
    public async Task DeployYaml_InvalidYaml_ReturnsFailureExitCode()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-fail-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var invalidYaml = $@"apiVersion: v1
kind: InvalidResourceKindThatDoesNotExist
metadata:
  name: should-fail
  namespace: {testNs}
data:
  key: value";

            var scriptResult = await PrepareAndExecuteYamlAsync(
                invalidYaml, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldNotBe(0, "Invalid resource kind should cause kubectl apply to fail");
            (scriptResult.StdErr + scriptResult.StdOut).ShouldContain("error");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployYaml_NonexistentNamespace_Fails()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var nonexistentNs = $"squid-noexist-{Guid.NewGuid().ToString("N")[..8]}";

        var yaml = $@"apiVersion: v1
kind: ConfigMap
metadata:
  name: should-fail
  namespace: {nonexistentNs}
data:
  key: value";

        // Use "default" as context namespace — the YAML itself targets a namespace that doesn't exist
        var scriptResult = await PrepareAndExecuteYamlAsync(
            yaml, clusterUrl, token, "default");

        scriptResult.ExitCode.ShouldNotBe(0, "Targeting a non-existent namespace should fail");
        (scriptResult.StdErr + scriptResult.StdOut).ShouldContain("not found");
    }

    [Fact]
    public async Task ContextScript_InvalidToken_AuthFails()
    {
        var clusterUrl = await GetClusterUrlAsync();

        var scriptContext = MakeScriptContext(clusterUrl, "this-is-an-invalid-token", "default");

        var script = _contextBuilder.WrapWithContext("kubectl get pods", scriptContext);

        var result = await ExecuteBashScriptAsync(script);

        result.ExitCode.ShouldNotBe(0, "Invalid token should cause authentication failure");
    }

    [Fact]
    public async Task ContextScript_InvalidCertificate_AuthFails()
    {
        var clusterUrl = await GetClusterUrlAsync();

        var endpoint = new EndpointContext
        {
            EndpointJson = JsonSerializer.Serialize(new KubernetesApiEndpointDto
            {
                ClusterUrl = clusterUrl,
                Namespace = "default",
                SkipTlsVerification = "True"
            })
        };
        endpoint.SetAccountData(AccountType.ClientCertificate, DeploymentAccountCredentialsConverter.Serialize(
            new ClientCertificateCredentials
            {
                ClientCertificateData = "bm90LWEtdmFsaWQtY2VydA==",
                ClientCertificateKeyData = "bm90LWEtdmFsaWQta2V5"
            }));

        var scriptContext = new ScriptContext
        {
            Endpoint = endpoint,
            Syntax = ScriptSyntax.Bash
        };

        var script = _contextBuilder.WrapWithContext("kubectl get pods", scriptContext);

        var result = await ExecuteBashScriptAsync(script);

        result.ExitCode.ShouldNotBe(0, "Invalid certificate should cause authentication failure");
    }

    [Fact]
    public async Task HelmUpgrade_NonexistentChart_Fails()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-helm-fail-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            var action = CreateHelmAction(new Dictionary<string, string>
            {
                ["Squid.Action.Script.Syntax"] = "Bash",
                ["Squid.Action.Helm.ReleaseName"] = $"e2e-fail-{Guid.NewGuid().ToString("N")[..6]}",
                ["Squid.Action.Helm.ChartPath"] = "nonexistent-repo/nonexistent-chart",
                ["Squid.Action.Kubernetes.Namespace"] = testNs,
                ["Squid.Action.Helm.AdditionalArgs"] = "--timeout 30s"
            });

            var ctx = new ActionExecutionContext { Action = action };
            var result = await _helmHandler.PrepareAsync(ctx, CancellationToken.None);

            var scriptContext = MakeScriptContext(clusterUrl, token, testNs);

            var fullScript = _contextBuilder.WrapWithContext(result.ScriptBody, scriptContext);

            var scriptResult = await ExecuteBashScriptAsync(fullScript);

            scriptResult.ExitCode.ShouldNotBe(0, "Non-existent chart should cause helm upgrade to fail");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task HelmUpgrade_InvalidValues_Fails()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-helm-fail-{Guid.NewGuid().ToString("N")[..8]}";
        var releaseName = $"e2e-badvals-{Guid.NewGuid().ToString("N")[..6]}";

        try
        {
            await ExecuteBashAsync(
                "helm repo add bitnami https://charts.bitnami.com/bitnami --force-update 2>/dev/null || true");

            var action = CreateHelmAction(new Dictionary<string, string>
            {
                ["Squid.Action.Script.Syntax"] = "Bash",
                ["Squid.Action.Helm.ReleaseName"] = releaseName,
                ["Squid.Action.Helm.ChartPath"] = "bitnami/nginx",
                ["Squid.Action.Kubernetes.Namespace"] = testNs,
                ["Squid.Action.Helm.YamlValues"] = "{{{{invalid yaml: [broken",
                ["Squid.Action.Helm.AdditionalArgs"] = "--timeout 30s"
            });

            var ctx = new ActionExecutionContext { Action = action };
            var result = await _helmHandler.PrepareAsync(ctx, CancellationToken.None);

            var tempDir = Path.Combine(Path.GetTempPath(), $"squid-helm-fail-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            foreach (var file in result.Files)
                await File.WriteAllBytesAsync(Path.Combine(tempDir, file.Key), file.Value);

            var modifiedScript = $"cd \"{tempDir}\"\n{result.ScriptBody}";
            var scriptContext = MakeScriptContext(clusterUrl, token, testNs);

            var fullScript = _contextBuilder.WrapWithContext(modifiedScript, scriptContext);

            var scriptResult = await ExecuteBashScriptAsync(fullScript);

            scriptResult.ExitCode.ShouldNotBe(0, "Invalid values YAML should cause helm upgrade to fail");
        }
        finally
        {
            await ExecuteBashAsync($"helm uninstall {releaseName} -n {testNs} 2>/dev/null || true");
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private async Task<ScriptResult> PrepareAndExecuteYamlAsync(
        string yaml, string clusterUrl, string token, string contextNamespace)
    {
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

        var tempDir = Path.Combine(Path.GetTempPath(), $"squid-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        foreach (var file in result.Files)
            await File.WriteAllBytesAsync(Path.Combine(tempDir, file.Key), file.Value);

        var modifiedScript = $"cd \"{tempDir}\"\n{result.ScriptBody}";
        var scriptContext = MakeScriptContext(clusterUrl, token, contextNamespace);

        var fullScript = _contextBuilder.WrapWithContext(modifiedScript, scriptContext);

        return await ExecuteBashScriptAsync(fullScript);
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

    private static DeploymentActionDto CreateHelmAction(Dictionary<string, string> properties)
    {
        var action = new DeploymentActionDto
        {
            ActionType = "Squid.HelmChartUpgrade",
            Properties = new List<DeploymentActionPropertyDto>()
        };

        foreach (var kvp in properties)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = kvp.Key,
                PropertyValue = kvp.Value
            });
        }

        return action;
    }
}
