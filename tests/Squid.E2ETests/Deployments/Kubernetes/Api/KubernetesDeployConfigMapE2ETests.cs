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

public class KubernetesDeployConfigMapE2ETests : KubernetesApiE2ETestBase
{
    private readonly KubernetesApiContextScriptBuilder _contextBuilder = new();
    private readonly KubernetesDeployConfigMapActionHandler _handler = new();

    public KubernetesDeployConfigMapE2ETests(KindClusterFixture cluster) : base(cluster)
    {
    }

    [Fact]
    public async Task DeployConfigMap_SingleEntry_AppliesSuccessfully()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-cm-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var action = BuildConfigMapAction("e2e-cm-single", testNs,
                "[{\"Key\":\"app-mode\",\"Value\":\"production\"}]");

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy ConfigMap failed: {scriptResult.StdErr}");

            var cm = await Cluster.KubectlAsync($"-n {testNs} get configmap e2e-cm-single -o yaml");
            cm.ShouldContain("app-mode");
            cm.ShouldContain("production");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployConfigMap_MultipleEntries_AllDataPresent()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-cm-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var action = BuildConfigMapAction("e2e-cm-multi", testNs,
                "[{\"Key\":\"db-host\",\"Value\":\"postgres.svc\"},{\"Key\":\"db-port\",\"Value\":\"5432\"},{\"Key\":\"log-level\",\"Value\":\"info\"}]");

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy ConfigMap failed: {scriptResult.StdErr}");

            var cm = await Cluster.KubectlAsync($"-n {testNs} get configmap e2e-cm-multi -o jsonpath='{{.data}}'");
            cm.ShouldContain("db-host");
            cm.ShouldContain("postgres.svc");
            cm.ShouldContain("db-port");
            cm.ShouldContain("5432");
            cm.ShouldContain("log-level");
            cm.ShouldContain("info");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployConfigMap_MultilineValue_PreservesBlockScalar()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-cm-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var multilineValue = "server {\n  listen 80;\n  location / {\n    proxy_pass http://backend;\n  }\n}";
            var valuesJson = JsonSerializer.Serialize(new[] { new { Key = "nginx.conf", Value = multilineValue } });

            var action = BuildConfigMapAction("e2e-cm-multiline", testNs, valuesJson);

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy ConfigMap failed: {scriptResult.StdErr}");

            var cm = await Cluster.KubectlAsync($"-n {testNs} get configmap e2e-cm-multiline -o jsonpath='{{.data.nginx\\.conf}}'");
            cm.ShouldContain("listen 80");
            cm.ShouldContain("proxy_pass");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployConfigMap_ObjectFormatValues_AppliesSuccessfully()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-cm-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var action = BuildConfigMapAction("e2e-cm-obj", testNs,
                "{\"env\":\"staging\",\"region\":\"us-west-2\"}");

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy ConfigMap failed: {scriptResult.StdErr}");

            var cm = await Cluster.KubectlAsync($"-n {testNs} get configmap e2e-cm-obj -o jsonpath='{{.data}}'");
            cm.ShouldContain("env");
            cm.ShouldContain("staging");
            cm.ShouldContain("region");
            cm.ShouldContain("us-west-2");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployConfigMap_Update_OverwritesExisting()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-cm-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var firstAction = BuildConfigMapAction("e2e-cm-update", testNs,
                "[{\"Key\":\"version\",\"Value\":\"v1\"}]");
            var firstResult = await PrepareAndAssertNotNull(firstAction);
            var firstScript = await ApplyToClusterAsync(firstResult, clusterUrl, token, testNs);
            firstScript.ExitCode.ShouldBe(0, $"First apply failed: {firstScript.StdErr}");

            var secondAction = BuildConfigMapAction("e2e-cm-update", testNs,
                "[{\"Key\":\"version\",\"Value\":\"v2\"}]");
            var secondResult = await PrepareAndAssertNotNull(secondAction);
            var secondScript = await ApplyToClusterAsync(secondResult, clusterUrl, token, testNs);
            secondScript.ExitCode.ShouldBe(0, $"Second apply failed: {secondScript.StdErr}");

            var cm = await Cluster.KubectlAsync($"-n {testNs} get configmap e2e-cm-update -o jsonpath='{{.data.version}}'");
            cm.Trim('\'').ShouldBe("v2");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployConfigMap_SpecialCharactersInValue_AppliesSuccessfully()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-cm-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var valuesJson = JsonSerializer.Serialize(new[]
            {
                new { Key = "connection-string", Value = "host=db;port=5432;user=admin;password=p@ss:w0rd!#" },
                new { Key = "json-config", Value = "{\"key\": \"value\", \"nested\": {\"flag\": true}}" }
            });

            var action = BuildConfigMapAction("e2e-cm-special", testNs, valuesJson);

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy ConfigMap failed: {scriptResult.StdErr}");

            var cm = await Cluster.KubectlAsync($"-n {testNs} get configmap e2e-cm-special -o yaml");
            cm.ShouldContain("connection-string");
            cm.ShouldContain("json-config");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployConfigMap_NoName_ReturnsNull()
    {
        var action = BuildConfigMapAction("", "default",
            "[{\"Key\":\"k\",\"Value\":\"v\"}]");

        var ctx = new ActionExecutionContext { Action = action };
        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task DeployConfigMap_NoValues_ReturnsNull()
    {
        var action = BuildConfigMapAction("some-cm", "default", "[]");

        var ctx = new ActionExecutionContext { Action = action };
        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task DeployConfigMap_LowercaseKeyValueFormat_AppliesSuccessfully()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-cm-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var action = BuildConfigMapAction("e2e-cm-lower", testNs,
                "[{\"key\":\"setting\",\"value\":\"enabled\"}]");

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy ConfigMap failed: {scriptResult.StdErr}");

            var cm = await Cluster.KubectlAsync($"-n {testNs} get configmap e2e-cm-lower -o jsonpath='{{.data.setting}}'");
            cm.Trim('\'').ShouldBe("enabled");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static DeploymentActionDto BuildConfigMapAction(string configMapName, string namespaceName, string valuesJson)
    {
        var properties = new List<DeploymentActionPropertyDto>
        {
            new() { PropertyName = "Squid.Action.KubernetesContainers.ConfigMapName", PropertyValue = configMapName },
            new() { PropertyName = "Squid.Action.Kubernetes.Namespace", PropertyValue = namespaceName }
        };

        if (valuesJson != null)
            properties.Add(new() { PropertyName = "Squid.Action.KubernetesContainers.ConfigMapValues", PropertyValue = valuesJson });

        return new DeploymentActionDto
        {
            ActionType = "Squid.KubernetesDeployConfigMap",
            Properties = properties
        };
    }

    private async Task<ActionExecutionResult> PrepareAndAssertNotNull(DeploymentActionDto action)
    {
        var ctx = new ActionExecutionContext { Action = action };
        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Files.ShouldContainKey("configmap.yaml");

        return result;
    }

    private async Task<ScriptResult> ApplyToClusterAsync(ActionExecutionResult result, string clusterUrl, string token, string ns)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"squid-cm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            foreach (var file in result.Files)
                await File.WriteAllBytesAsync(Path.Combine(tempDir, file.Key), file.Value);

            var modifiedScript = $"cd \"{tempDir}\"\n{result.ScriptBody}";
            var scriptContext = MakeScriptContext(clusterUrl, token, ns);
            var fullScript = _contextBuilder.WrapWithContext(modifiedScript, scriptContext);

            return await ExecuteBashScriptAsync(fullScript);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
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
