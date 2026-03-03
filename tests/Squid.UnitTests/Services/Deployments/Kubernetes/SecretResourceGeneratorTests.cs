using System.Text;
using System.Text.Json;
using System.Threading;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class SecretResourceGeneratorTests
{
    private readonly KubernetesContainersActionYamlGenerator _compositor = new();

    // === Kind / apiVersion / type ===

    [Fact]
    public async Task Generate_BasicSecret_HasCorrectApiVersionAndKind()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetSecretYaml(step, action);

        yaml.ShouldContain("apiVersion: v1");
        yaml.ShouldContain("kind: Secret");
    }

    [Fact]
    public async Task Generate_BasicSecret_TypeIsOpaque()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetSecretYaml(step, action);

        yaml.ShouldContain("type: Opaque");
    }

    [Fact]
    public async Task Generate_BasicSecret_UsesStringDataSection()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetSecretYaml(step, action);

        yaml.ShouldContain("stringData:");
    }

    // === Metadata ===

    [Fact]
    public async Task Generate_SecretName_IsIncluded()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetSecretYaml(step, action);

        yaml.ShouldContain("name: my-secret");
    }

    [Fact]
    public async Task Generate_SecretNamespace_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.Namespace", "secure-ns");

        var yaml = await GetSecretYaml(step, action);

        yaml.ShouldContain("namespace: secure-ns");
    }

    // === Data values — simple ===

    [Fact]
    public async Task Generate_SimpleValue_WrittenUnquoted()
    {
        var (step, action) = CreateWith("DB_USER", "admin");

        var yaml = await GetSecretYaml(step, action);

        yaml.ShouldContain("DB_USER: admin");
    }

    // === Value quoting — booleans / nulls ===

    [Theory]
    [InlineData("true", "'true'")]
    [InlineData("false", "'false'")]
    [InlineData("null", "'null'")]
    [InlineData("yes", "'yes'")]
    [InlineData("no", "'no'")]
    public async Task Generate_BooleanOrNullLikeValue_IsQuoted(string rawValue, string expected)
    {
        var (step, action) = CreateWith("MY_KEY", rawValue);

        var yaml = await GetSecretYaml(step, action);

        yaml.ShouldContain($"MY_KEY: {expected}");
    }

    // === Value quoting — numerics ===

    [Theory]
    [InlineData("8080", "'8080'")]
    [InlineData("3.14", "'3.14'")]
    [InlineData("0", "'0'")]
    public async Task Generate_NumericValue_IsQuoted(string rawValue, string expected)
    {
        var (step, action) = CreateWith("PORT", rawValue);

        var yaml = await GetSecretYaml(step, action);

        yaml.ShouldContain($"PORT: {expected}");
    }

    // === Value quoting — special characters ===

    [Fact]
    public async Task Generate_ValueWithColonSpace_IsQuoted()
    {
        var (step, action) = CreateWith("CONN_STR", "Server=host: 1433;Database=db");

        var yaml = await GetSecretYaml(step, action);

        yaml.ShouldContain("CONN_STR: 'Server=host: 1433;Database=db'");
    }

    // === Empty values ===

    [Fact]
    public async Task Generate_EmptyValue_WrittenAsSingleQuotedEmptyString()
    {
        var (step, action) = CreateWith("EMPTY_SECRET", "");

        var yaml = await GetSecretYaml(step, action);

        yaml.ShouldContain("EMPTY_SECRET: ''");
    }

    // === Multiple values ===

    [Fact]
    public async Task Generate_MultipleValues_AllWritten()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetSecretYaml(step, action);

        yaml.ShouldContain("DB_USER: admin");
        yaml.ShouldContain("DB_PASS: secret123");
    }

    // === JSON format ===

    [Fact]
    public async Task Generate_ArrayJsonFormat_GeneratesCorrectly()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetSecretYaml(step, action);

        yaml.ShouldContain("DB_USER: admin");
    }

    [Fact]
    public async Task Generate_ObjectJsonFormat_GeneratesCorrectly()
    {
        var (step, action) = CreateMinimalWithObjectFormat();

        var yaml = await GetSecretYaml(step, action);

        yaml.ShouldContain("API_KEY: my-api-key");
        yaml.ShouldContain("TOKEN: my-token");
    }

    [Fact]
    public async Task Generate_LowerCaseKeyValueJsonFormat_GeneratesCorrectly()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers", Name = "test" };
        Add(action, "Squid.Action.KubernetesContainers.SecretName", "my-secret");
        Add(action, "Squid.Action.KubernetesContainers.SecretValues",
            """[{"key":"LOWER_KEY","value":"lower-val"}]""");

        var yaml = await GetSecretYaml(step, action);

        yaml.ShouldContain("LOWER_KEY: lower-val");
    }

    // === CanGenerate guards ===

    [Fact]
    public async Task Generate_NoSecretName_SecretYamlNotGenerated()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers", Name = "test" };
        Add(action, "Squid.Action.KubernetesContainers.SecretValues",
            """[{"Key":"A","Value":"1"}]""");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldNotContainKey("secret.yaml");
    }

    [Fact]
    public async Task Generate_EmptyValuesArray_SecretYamlNotGenerated()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers", Name = "test" };
        Add(action, "Squid.Action.KubernetesContainers.SecretName", "my-secret");
        Add(action, "Squid.Action.KubernetesContainers.SecretValues", "[]");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldNotContainKey("secret.yaml");
    }

    // === Helpers ===

    private async Task<string> GetSecretYaml(DeploymentStepDto step, DeploymentActionDto action)
    {
        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        result.ShouldContainKey("secret.yaml");
        return Encoding.UTF8.GetString(result["secret.yaml"]);
    }

    private static (DeploymentStepDto step, DeploymentActionDto action) CreateMinimal()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto
        {
            ActionType = "Squid.KubernetesDeployContainers",
            Name = "test-deploy"
        };

        Add(action, "Squid.Action.KubernetesContainers.SecretName", "my-secret");
        Add(action, "Squid.Action.KubernetesContainers.SecretValues",
            """[{"Key":"DB_USER","Value":"admin"},{"Key":"DB_PASS","Value":"secret123"}]""");

        return (step, action);
    }

    private static (DeploymentStepDto step, DeploymentActionDto action) CreateWith(string key, string value)
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto
        {
            ActionType = "Squid.KubernetesDeployContainers",
            Name = "test-deploy"
        };

        Add(action, "Squid.Action.KubernetesContainers.SecretName", "my-secret");
        Add(action, "Squid.Action.KubernetesContainers.SecretValues",
            $"[{{\"Key\":\"{key}\",\"Value\":{JsonSerializer.Serialize(value)}}}]");

        return (step, action);
    }

    private static (DeploymentStepDto step, DeploymentActionDto action) CreateMinimalWithObjectFormat()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto
        {
            ActionType = "Squid.KubernetesDeployContainers",
            Name = "test-deploy"
        };

        Add(action, "Squid.Action.KubernetesContainers.SecretName", "my-secret");
        Add(action, "Squid.Action.KubernetesContainers.SecretValues",
            """{"API_KEY":"my-api-key","TOKEN":"my-token"}""");

        return (step, action);
    }

    private static void Add(DeploymentActionDto action, string name, string value)
    {
        action.Properties.Add(new DeploymentActionPropertyDto
        {
            PropertyName = name,
            PropertyValue = value
        });
    }
}
