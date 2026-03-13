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
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.E2ETests.Deployments.Kubernetes.Api;

public class KubernetesDeployIngressE2ETests : KubernetesApiE2ETestBase
{
    private readonly KubernetesApiContextScriptBuilder _contextBuilder = new();
    private readonly KubernetesDeployIngressActionHandler _handler = new();

    public KubernetesDeployIngressE2ETests(KindClusterFixture cluster) : base(cluster)
    {
    }

    [Fact]
    public async Task DeployIngress_BasicRules_Bash_AppliesSuccessfully()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-ing-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var action = BuildIngressAction(
                ingressName: "basic-ingress",
                namespaceName: testNs,
                rulesJson: $"[{{\"host\":\"basic.example.com\",\"http\":{{\"paths\":[{{\"path\":\"/\",\"pathType\":\"Prefix\",\"backend\":{{\"service\":{{\"name\":\"web-svc\",\"port\":{{\"number\":80}}}}}}}}]}}}}]");

            var ctx = new ActionExecutionContext { Action = action };
            var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

            result.ShouldNotBeNull();

            var tempDir = await WriteFilesAndWrapScriptAsync(result, clusterUrl, token, testNs);

            try
            {
                var scriptResult = await ExecuteBashScriptAsync(tempDir.Script);
                scriptResult.ExitCode.ShouldBe(0, $"Deploy ingress failed: {scriptResult.StdErr}");

                var ingressOutput = await Cluster.KubectlAsync(
                    $"-n {testNs} get ingress basic-ingress -o jsonpath='{{.metadata.name}}'");
                ingressOutput.Trim('\'').ShouldBe("basic-ingress");
            }
            finally
            {
                Directory.Delete(tempDir.Dir, recursive: true);
            }
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployIngress_WithAnnotationsAndTls_AppliesSuccessfully()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-ingtls-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var action = BuildIngressAction(
                ingressName: "tls-ingress",
                namespaceName: testNs,
                rulesJson: $"[{{\"host\":\"secure.example.com\",\"http\":{{\"paths\":[{{\"path\":\"/\",\"pathType\":\"Prefix\",\"backend\":{{\"service\":{{\"name\":\"web-svc\",\"port\":{{\"number\":443}}}}}}}}]}}}}]",
                annotationsJson: "{\"nginx.ingress.kubernetes.io/ssl-redirect\": \"true\"}",
                tlsJson: "[{\"hosts\":[\"secure.example.com\"],\"secretName\":\"tls-cert\"}]");

            var ctx = new ActionExecutionContext { Action = action };
            var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

            result.ShouldNotBeNull();

            var tempDir = await WriteFilesAndWrapScriptAsync(result, clusterUrl, token, testNs);

            try
            {
                var scriptResult = await ExecuteBashScriptAsync(tempDir.Script);
                scriptResult.ExitCode.ShouldBe(0, $"Deploy TLS ingress failed: {scriptResult.StdErr}");

                var annotations = await Cluster.KubectlAsync(
                    $"-n {testNs} get ingress tls-ingress -o jsonpath='{{.metadata.annotations}}'");
                annotations.ShouldContain("ssl-redirect");

                var tlsHosts = await Cluster.KubectlAsync(
                    $"-n {testNs} get ingress tls-ingress -o jsonpath='{{.spec.tls[0].hosts[0]}}'");
                tlsHosts.Trim('\'').ShouldBe("secure.example.com");
            }
            finally
            {
                Directory.Delete(tempDir.Dir, recursive: true);
            }
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployIngress_WithIngressClassName_AppliesSuccessfully()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-ingcls-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var action = BuildIngressAction(
                ingressName: "class-ingress",
                namespaceName: testNs,
                rulesJson: $"[{{\"host\":\"class.example.com\",\"http\":{{\"paths\":[{{\"path\":\"/\",\"pathType\":\"Prefix\",\"backend\":{{\"service\":{{\"name\":\"web-svc\",\"port\":{{\"number\":80}}}}}}}}]}}}}]",
                ingressClassName: "nginx");

            var ctx = new ActionExecutionContext { Action = action };
            var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

            result.ShouldNotBeNull();

            var tempDir = await WriteFilesAndWrapScriptAsync(result, clusterUrl, token, testNs);

            try
            {
                var scriptResult = await ExecuteBashScriptAsync(tempDir.Script);
                scriptResult.ExitCode.ShouldBe(0, $"Deploy className ingress failed: {scriptResult.StdErr}");

                var className = await Cluster.KubectlAsync(
                    $"-n {testNs} get ingress class-ingress -o jsonpath='{{.spec.ingressClassName}}'");
                className.Trim('\'').ShouldBe("nginx");
            }
            finally
            {
                Directory.Delete(tempDir.Dir, recursive: true);
            }
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployIngress_NoRules_ReturnsNull()
    {
        var action = BuildIngressAction(
            ingressName: "no-rules",
            namespaceName: "default",
            rulesJson: null);

        var ctx = new ActionExecutionContext { Action = action };
        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task DeployIngress_MultipleHosts_AppliesSuccessfully()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-ingmh-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var rulesJson = "[" +
                "{\"host\":\"api.example.com\",\"http\":{\"paths\":[{\"path\":\"/\",\"pathType\":\"Prefix\",\"backend\":{\"service\":{\"name\":\"api-svc\",\"port\":{\"number\":80}}}}]}}," +
                "{\"host\":\"web.example.com\",\"http\":{\"paths\":[{\"path\":\"/\",\"pathType\":\"Prefix\",\"backend\":{\"service\":{\"name\":\"web-svc\",\"port\":{\"number\":80}}}}]}}" +
                "]";

            var action = BuildIngressAction(
                ingressName: "multi-ingress",
                namespaceName: testNs,
                rulesJson: rulesJson);

            var ctx = new ActionExecutionContext { Action = action };
            var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

            result.ShouldNotBeNull();

            var tempDir = await WriteFilesAndWrapScriptAsync(result, clusterUrl, token, testNs);

            try
            {
                var scriptResult = await ExecuteBashScriptAsync(tempDir.Script);
                scriptResult.ExitCode.ShouldBe(0, $"Deploy multi-host ingress failed: {scriptResult.StdErr}");

                var ingressYaml = await Cluster.KubectlAsync(
                    $"-n {testNs} get ingress multi-ingress -o yaml");
                ingressYaml.ShouldContain("api.example.com");
                ingressYaml.ShouldContain("web.example.com");
            }
            finally
            {
                Directory.Delete(tempDir.Dir, recursive: true);
            }
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static DeploymentActionDto BuildIngressAction(
        string ingressName,
        string namespaceName,
        string rulesJson,
        string annotationsJson = null,
        string tlsJson = null,
        string ingressClassName = null)
    {
        var properties = new List<DeploymentActionPropertyDto>
        {
            new() { PropertyName = "Squid.Action.KubernetesContainers.IngressName", PropertyValue = ingressName },
            new() { PropertyName = "Squid.Action.Kubernetes.Namespace", PropertyValue = namespaceName }
        };

        if (rulesJson != null)
            properties.Add(new() { PropertyName = "Squid.Action.KubernetesContainers.IngressRules", PropertyValue = rulesJson });

        if (annotationsJson != null)
            properties.Add(new() { PropertyName = "Squid.Action.KubernetesContainers.IngressAnnotations", PropertyValue = annotationsJson });

        if (tlsJson != null)
            properties.Add(new() { PropertyName = "Squid.Action.KubernetesContainers.IngressTlsCertificates", PropertyValue = tlsJson });

        if (ingressClassName != null)
            properties.Add(new() { PropertyName = "Squid.Action.KubernetesContainers.IngressClassName", PropertyValue = ingressClassName });

        return new DeploymentActionDto
        {
            ActionType = "Squid.KubernetesDeployIngress",
            Properties = properties
        };
    }

    private async Task<(string Dir, string Script)> WriteFilesAndWrapScriptAsync(
        ActionExecutionResult result, string clusterUrl, string token, string ns)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"squid-ing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        foreach (var file in result.Files)
            await File.WriteAllBytesAsync(Path.Combine(tempDir, file.Key), file.Value);

        var modifiedScript = $"cd \"{tempDir}\"\n{result.ScriptBody}";
        var scriptContext = MakeScriptContext(clusterUrl, token, ns);
        var fullScript = _contextBuilder.WrapWithContext(modifiedScript, scriptContext);

        return (tempDir, fullScript);
    }

    private static ScriptContext MakeScriptContext(string clusterUrl, string token, string ns)
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
