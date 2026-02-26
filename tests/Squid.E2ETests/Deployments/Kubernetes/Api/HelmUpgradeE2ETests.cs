using System.Text;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
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

public class HelmUpgradeE2ETests : KubernetesApiE2ETestBase
{
    private readonly KubernetesApiContextScriptBuilder _contextBuilder = new();
    private readonly HelmUpgradeActionHandler _helmHandler = new();

    public HelmUpgradeE2ETests(KindClusterFixture cluster) : base(cluster)
    {
    }

    [Fact(Skip = "Requires helm CLI to be installed")]
    public async Task HelmUpgrade_NginxChart_Bash_Succeeds()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-helm-{Guid.NewGuid().ToString("N")[..8]}";
        var releaseName = $"e2e-nginx-{Guid.NewGuid().ToString("N")[..6]}";

        try
        {
            // Add bitnami repo
            await ExecuteBashAsync("helm repo add bitnami https://charts.bitnami.com/bitnami --force-update 2>/dev/null || true");
            await ExecuteBashAsync("helm repo update");

            // Prepare Helm action
            var action = CreateHelmAction(new Dictionary<string, string>
            {
                ["Squid.Action.Script.Syntax"] = "Bash",
                ["Squid.Action.Helm.ReleaseName"] = releaseName,
                ["Squid.Action.Helm.ChartPath"] = "bitnami/nginx",
                ["Squid.Action.Kubernetes.Namespace"] = testNs,
                ["Squid.Action.Helm.ResetValues"] = "True",
                ["Squid.Action.Helm.AdditionalArgs"] = "--set service.type=ClusterIP --timeout 120s"
            });

            var ctx = new ActionExecutionContext { Action = action };
            var result = await _helmHandler.PrepareAsync(ctx, CancellationToken.None);

            // Wrap with K8s context
            var endpoint = new KubernetesApiEndpointDto
            {
                ClusterUrl = clusterUrl,
                Namespace = testNs,
                SkipTlsVerification = "True"
            };
            var account = new DeploymentAccount
            {
                AccountType = AccountType.Token,
                Credentials = DeploymentAccountCredentialsConverter.Serialize(
                    new TokenCredentials { Token = token })
            };

            var fullScript = _contextBuilder.WrapWithContext(
                result.ScriptBody,
                endpoint,
                account,
                ScriptSyntax.Bash);

            var scriptResult = await ExecuteBashScriptAsync(fullScript);
            scriptResult.ExitCode.ShouldBe(0, $"Helm upgrade failed: {scriptResult.StdErr}");

            // Verify the release exists
            var releases = await Cluster.KubectlAsync($"-n {testNs} get pods");
            releases.ShouldContain("nginx");
        }
        finally
        {
            // Cleanup
            await ExecuteBashAsync($"helm uninstall {releaseName} -n {testNs} 2>/dev/null || true");
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact(Skip = "Requires helm CLI to be installed")]
    public async Task HelmUpgrade_WithYamlValues_Bash_AppliesValues()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-helm-{Guid.NewGuid().ToString("N")[..8]}";
        var releaseName = $"e2e-vals-{Guid.NewGuid().ToString("N")[..6]}";

        try
        {
            await ExecuteBashAsync("helm repo add bitnami https://charts.bitnami.com/bitnami --force-update 2>/dev/null || true");

            var action = CreateHelmAction(new Dictionary<string, string>
            {
                ["Squid.Action.Script.Syntax"] = "Bash",
                ["Squid.Action.Helm.ReleaseName"] = releaseName,
                ["Squid.Action.Helm.ChartPath"] = "bitnami/nginx",
                ["Squid.Action.Kubernetes.Namespace"] = testNs,
                ["Squid.Action.Helm.YamlValues"] = "replicaCount: 1\nservice:\n  type: ClusterIP",
                ["Squid.Action.Helm.AdditionalArgs"] = "--timeout 120s"
            });

            var ctx = new ActionExecutionContext { Action = action };
            var result = await _helmHandler.PrepareAsync(ctx, CancellationToken.None);

            // Verify the values file was created
            result.Files.ShouldContainKey("rawYamlValues.yaml");
            var valuesContent = Encoding.UTF8.GetString(result.Files["rawYamlValues.yaml"]);
            valuesContent.ShouldContain("replicaCount: 1");

            // Wrap with K8s context and execute
            var endpoint = new KubernetesApiEndpointDto
            {
                ClusterUrl = clusterUrl,
                Namespace = testNs,
                SkipTlsVerification = "True"
            };
            var account = new DeploymentAccount
            {
                AccountType = AccountType.Token,
                Credentials = DeploymentAccountCredentialsConverter.Serialize(
                    new TokenCredentials { Token = token })
            };

            // Write values file to temp dir and modify script to reference it
            var tempDir = Path.Combine(Path.GetTempPath(), $"squid-helm-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            foreach (var file in result.Files)
            {
                await File.WriteAllBytesAsync(Path.Combine(tempDir, file.Key), file.Value);
            }

            // Prepend cd to temp dir in script so values file is found
            var modifiedScript = $"cd \"{tempDir}\"\n{result.ScriptBody}";

            var fullScript = _contextBuilder.WrapWithContext(
                modifiedScript,
                endpoint,
                account,
                ScriptSyntax.Bash);

            var scriptResult = await ExecuteBashScriptAsync(fullScript);
            scriptResult.ExitCode.ShouldBe(0, $"Helm upgrade failed: {scriptResult.StdErr}");
        }
        finally
        {
            await ExecuteBashAsync($"helm uninstall {releaseName} -n {testNs} 2>/dev/null || true");
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact(Skip = "Requires helm CLI to be installed")]
    public async Task HelmUpgrade_DryRun_Bash_ShowsManifest()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-helm-{Guid.NewGuid().ToString("N")[..8]}";
        var releaseName = $"e2e-dry-{Guid.NewGuid().ToString("N")[..6]}";

        try
        {
            await ExecuteBashAsync("helm repo add bitnami https://charts.bitnami.com/bitnami --force-update 2>/dev/null || true");

            var action = CreateHelmAction(new Dictionary<string, string>
            {
                ["Squid.Action.Script.Syntax"] = "Bash",
                ["Squid.Action.Helm.ReleaseName"] = releaseName,
                ["Squid.Action.Helm.ChartPath"] = "bitnami/nginx",
                ["Squid.Action.Kubernetes.Namespace"] = testNs,
                ["Squid.Action.Helm.AdditionalArgs"] = "--dry-run --set service.type=ClusterIP"
            });

            var ctx = new ActionExecutionContext { Action = action };
            var result = await _helmHandler.PrepareAsync(ctx, CancellationToken.None);

            var endpoint = new KubernetesApiEndpointDto
            {
                ClusterUrl = clusterUrl,
                Namespace = testNs,
                SkipTlsVerification = "True"
            };
            var account = new DeploymentAccount
            {
                AccountType = AccountType.Token,
                Credentials = DeploymentAccountCredentialsConverter.Serialize(
                    new TokenCredentials { Token = token })
            };

            var fullScript = _contextBuilder.WrapWithContext(
                result.ScriptBody,
                endpoint,
                account,
                ScriptSyntax.Bash);

            var scriptResult = await ExecuteBashScriptAsync(fullScript);
            scriptResult.ExitCode.ShouldBe(0, $"Helm dry-run failed: {scriptResult.StdErr}");
            // Dry-run should output rendered manifests
            (scriptResult.StdOut + scriptResult.StdErr).ShouldContain("nginx");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    // === Helper Methods ===

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
