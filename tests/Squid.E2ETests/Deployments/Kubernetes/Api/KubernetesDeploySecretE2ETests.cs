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

public class KubernetesDeploySecretE2ETests : KubernetesApiE2ETestBase
{
    private readonly KubernetesApiContextScriptBuilder _contextBuilder = new();
    private readonly KubernetesDeploySecretActionHandler _handler = new();

    public KubernetesDeploySecretE2ETests(KindClusterFixture cluster) : base(cluster)
    {
    }

    [Fact]
    public async Task DeploySecret_ArrayFormat_SingleEntry_AppliesSuccessfully()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-sec-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var action = BuildSecretAction("e2e-sec-single", testNs,
                "[{\"Key\":\"api-key\",\"Value\":\"sk-abc123\"}]");

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy Secret failed: {scriptResult.StdErr}");

            var secret = await Cluster.KubectlAsync($"-n {testNs} get secret e2e-sec-single -o jsonpath='{{.data.api-key}}'");
            secret.Trim('\'').ShouldNotBeNullOrWhiteSpace();
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeploySecret_ArrayFormat_MultipleEntries_AllKeysPresent()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-sec-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var action = BuildSecretAction("e2e-sec-multi", testNs,
                "[{\"Key\":\"username\",\"Value\":\"admin\"},{\"Key\":\"password\",\"Value\":\"s3cret!\"},{\"Key\":\"host\",\"Value\":\"db.internal\"}]");

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy Secret failed: {scriptResult.StdErr}");

            var secretYaml = await Cluster.KubectlAsync($"-n {testNs} get secret e2e-sec-multi -o yaml");
            secretYaml.ShouldContain("username");
            secretYaml.ShouldContain("password");
            secretYaml.ShouldContain("host");

            var secretType = await Cluster.KubectlAsync($"-n {testNs} get secret e2e-sec-multi -o jsonpath='{{.type}}'");
            secretType.Trim('\'').ShouldBe("Opaque");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeploySecret_ObjectFormat_AppliesSuccessfully()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-sec-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var action = BuildSecretAction("e2e-sec-obj", testNs,
                "{\"db-user\":\"postgres\",\"db-pass\":\"p@ssw0rd\"}");

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy Secret failed: {scriptResult.StdErr}");

            var secretYaml = await Cluster.KubectlAsync($"-n {testNs} get secret e2e-sec-obj -o yaml");
            secretYaml.ShouldContain("db-user");
            secretYaml.ShouldContain("db-pass");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeploySecret_LowercaseKeyValueFormat_AppliesSuccessfully()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-sec-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var action = BuildSecretAction("e2e-sec-lower", testNs,
                "[{\"key\":\"token\",\"value\":\"xyz789\"}]");

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy Secret failed: {scriptResult.StdErr}");

            var secretYaml = await Cluster.KubectlAsync($"-n {testNs} get secret e2e-sec-lower -o yaml");
            secretYaml.ShouldContain("token");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeploySecret_Update_OverwritesExisting()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-sec-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var firstAction = BuildSecretAction("e2e-sec-update", testNs,
                "[{\"Key\":\"secret-val\",\"Value\":\"old-value\"}]");
            var firstResult = await PrepareAndAssertNotNull(firstAction);
            var firstScript = await ApplyToClusterAsync(firstResult, clusterUrl, token, testNs);
            firstScript.ExitCode.ShouldBe(0, $"First apply failed: {firstScript.StdErr}");

            var secondAction = BuildSecretAction("e2e-sec-update", testNs,
                "[{\"Key\":\"secret-val\",\"Value\":\"new-value\"}]");
            var secondResult = await PrepareAndAssertNotNull(secondAction);
            var secondScript = await ApplyToClusterAsync(secondResult, clusterUrl, token, testNs);
            secondScript.ExitCode.ShouldBe(0, $"Second apply failed: {secondScript.StdErr}");

            // Decode the base64 value to verify it was updated
            var b64Value = await Cluster.KubectlAsync($"-n {testNs} get secret e2e-sec-update -o jsonpath='{{.data.secret-val}}'");
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64Value.Trim('\'')));
            decoded.ShouldBe("new-value");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeploySecret_SpecialCharactersInValue_AppliesSuccessfully()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-sec-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var valuesJson = JsonSerializer.Serialize(new[]
            {
                new { Key = "cert", Value = "-----BEGIN CERTIFICATE-----\nMIIBxTCCAW...\n-----END CERTIFICATE-----" },
                new { Key = "conn", Value = "Server=db;Port=5432;Uid=root;Pwd=p@ss!$#%^&*()" }
            });

            var action = BuildSecretAction("e2e-sec-special", testNs, valuesJson);

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy Secret failed: {scriptResult.StdErr}");

            var secretYaml = await Cluster.KubectlAsync($"-n {testNs} get secret e2e-sec-special -o yaml");
            secretYaml.ShouldContain("cert");
            secretYaml.ShouldContain("conn");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeploySecret_NoName_ReturnsNull()
    {
        var action = BuildSecretAction("", "default",
            "[{\"Key\":\"k\",\"Value\":\"v\"}]");

        var ctx = new ActionExecutionContext { Action = action };
        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task DeploySecret_NoValues_ReturnsNull()
    {
        var action = BuildSecretAction("some-secret", "default", "");

        var ctx = new ActionExecutionContext { Action = action };
        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task DeploySecret_InvalidJson_ReturnsNull()
    {
        var action = BuildSecretAction("bad-json-secret", "default", "not-json");

        var ctx = new ActionExecutionContext { Action = action };
        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task DeploySecret_EmptyArrayValues_ReturnsNull()
    {
        var action = BuildSecretAction("empty-arr-secret", "default", "[]");

        var ctx = new ActionExecutionContext { Action = action };
        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static DeploymentActionDto BuildSecretAction(string secretName, string namespaceName, string valuesJson)
    {
        var properties = new List<DeploymentActionPropertyDto>
        {
            new() { PropertyName = "Squid.Action.KubernetesContainers.SecretName", PropertyValue = secretName },
            new() { PropertyName = "Squid.Action.Kubernetes.Namespace", PropertyValue = namespaceName }
        };

        if (valuesJson != null)
            properties.Add(new() { PropertyName = "Squid.Action.KubernetesContainers.SecretValues", PropertyValue = valuesJson });

        return new DeploymentActionDto
        {
            ActionType = "Squid.KubernetesDeploySecret",
            Properties = properties
        };
    }

    private async Task<ActionExecutionResult> PrepareAndAssertNotNull(DeploymentActionDto action)
    {
        var ctx = new ActionExecutionContext { Action = action };
        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Files.ShouldContainKey("secret.yaml");

        return result;
    }

    private async Task<ScriptResult> ApplyToClusterAsync(ActionExecutionResult result, string clusterUrl, string token, string ns)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"squid-sec-{Guid.NewGuid():N}");
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
