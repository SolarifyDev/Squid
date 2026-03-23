using System.Text;
using System.Text.Json;
using System.Threading;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class ConfigMapResourceGeneratorTests
{
    private readonly KubernetesContainersActionYamlGenerator _compositor = new();

    // === Kind / apiVersion ===

    [Fact]
    public async Task Generate_BasicConfigMap_HasCorrectApiVersionAndKind()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetConfigMapYaml(step, action);

        yaml.ShouldContain("apiVersion: v1");
        yaml.ShouldContain("kind: ConfigMap");
    }

    // === Metadata ===

    [Fact]
    public async Task Generate_ConfigMapName_IsIncluded()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetConfigMapYaml(step, action);

        yaml.ShouldContain("name: \"my-config\"");
    }

    [Fact]
    public async Task Generate_ConfigMapNamespace_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.Namespace", "prod-ns");

        var yaml = await GetConfigMapYaml(step, action);

        yaml.ShouldContain("namespace: \"prod-ns\"");
    }

    // === Data section ===

    [Fact]
    public async Task Generate_DataSection_IsPresent()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetConfigMapYaml(step, action);

        yaml.ShouldContain("data:");
    }

    [Fact]
    public async Task Generate_SimpleStringValue_WrittenUnquoted()
    {
        var (step, action) = CreateWith("APP_ENV", "production");

        var yaml = await GetConfigMapYaml(step, action);

        yaml.ShouldContain("\"APP_ENV\": production");
    }

    // === Value quoting — booleans / nulls ===

    [Theory]
    [InlineData("true", "'true'")]
    [InlineData("false", "'false'")]
    [InlineData("yes", "'yes'")]
    [InlineData("no", "'no'")]
    [InlineData("null", "'null'")]
    [InlineData("~", "'~'")]
    [InlineData("on", "'on'")]
    [InlineData("off", "'off'")]
    public async Task Generate_BooleanOrNullLikeValue_IsQuoted(string rawValue, string expected)
    {
        var (step, action) = CreateWith("MY_KEY", rawValue);

        var yaml = await GetConfigMapYaml(step, action);

        yaml.ShouldContain($"\"MY_KEY\": {expected}");
    }

    // === Value quoting — numerics ===

    [Theory]
    [InlineData("123", "'123'")]
    [InlineData("3.14", "'3.14'")]
    [InlineData("0", "'0'")]
    [InlineData("-42", "'-42'")]
    public async Task Generate_NumericValue_IsQuoted(string rawValue, string expected)
    {
        var (step, action) = CreateWith("PORT", rawValue);

        var yaml = await GetConfigMapYaml(step, action);

        yaml.ShouldContain($"\"PORT\": {expected}");
    }

    // === Value quoting — special characters ===

    [Fact]
    public async Task Generate_ValueWithColonSpace_IsQuoted()
    {
        var (step, action) = CreateWith("DB_URL", "postgres://host: 5432");

        var yaml = await GetConfigMapYaml(step, action);

        yaml.ShouldContain("\"DB_URL\": 'postgres://host: 5432'");
    }

    [Fact]
    public async Task Generate_ValueStartingWithDash_IsQuoted()
    {
        var (step, action) = CreateWith("ARG", "-Xmx512m");

        var yaml = await GetConfigMapYaml(step, action);

        yaml.ShouldContain("\"ARG\": '-Xmx512m'");
    }

    // === Empty values ===

    [Fact]
    public async Task Generate_EmptyValue_WrittenAsSingleQuotedEmptyString()
    {
        var (step, action) = CreateWith("EMPTY_KEY", "");

        var yaml = await GetConfigMapYaml(step, action);

        yaml.ShouldContain("\"EMPTY_KEY\": ''");
    }

    // === Multiline values ===

    [Fact]
    public async Task Generate_MultilineValue_UsesBlockScalar()
    {
        var (step, action) = CreateWith("SCRIPT", "line1\nline2\nline3");

        var yaml = await GetConfigMapYaml(step, action);

        yaml.ShouldContain("\"SCRIPT\": |");
        yaml.ShouldContain("    line1");
        yaml.ShouldContain("    line2");
        yaml.ShouldContain("    line3");
    }

    [Fact]
    public async Task Generate_MultilineValueWithCrlf_NormalizesToLf()
    {
        var (step, action) = CreateWith("CONF", "key=val1\r\nkey2=val2");

        var yaml = await GetConfigMapYaml(step, action);

        yaml.ShouldContain("\"CONF\": |");
        yaml.ShouldContain("    key=val1");
        yaml.ShouldContain("    key2=val2");
    }

    // === Multiple values ===

    [Fact]
    public async Task Generate_MultipleValues_AllWritten()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetConfigMapYaml(step, action);

        yaml.ShouldContain("\"APP_ENV\": production");
        yaml.ShouldContain("\"LOG_LEVEL\": info");
    }

    // === JSON format ===

    [Fact]
    public async Task Generate_ObjectJsonFormat_GeneratesCorrectly()
    {
        var (step, action) = CreateMinimalWithObjectFormat();

        var yaml = await GetConfigMapYaml(step, action);

        yaml.ShouldContain("\"KEY_A\": val-a");
        yaml.ShouldContain("\"KEY_B\": val-b");
    }

    // === CanGenerate guards ===

    [Fact]
    public async Task Generate_NoConfigMapName_ConfigMapYamlNotGenerated()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers", Name = "test" };
        Add(action, "Squid.Action.KubernetesContainers.ConfigMapValues",
            """[{"Key":"A","Value":"1"}]""");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldNotContainKey("configmap.yaml");
    }

    [Fact]
    public async Task Generate_EmptyValuesArray_ConfigMapYamlNotGenerated()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers", Name = "test" };
        Add(action, "Squid.Action.KubernetesContainers.ConfigMapName", "my-config");
        Add(action, "Squid.Action.KubernetesContainers.ConfigMapValues", "[]");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldNotContainKey("configmap.yaml");
    }

    // === Helpers ===

    private async Task<string> GetConfigMapYaml(DeploymentStepDto step, DeploymentActionDto action)
    {
        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        result.ShouldContainKey("configmap.yaml");
        return Encoding.UTF8.GetString(result["configmap.yaml"]);
    }

    private static (DeploymentStepDto step, DeploymentActionDto action) CreateMinimal()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto
        {
            ActionType = "Squid.KubernetesDeployContainers",
            Name = "test-deploy"
        };

        Add(action, "Squid.Action.KubernetesContainers.ConfigMapName", "my-config");
        Add(action, "Squid.Action.KubernetesContainers.ConfigMapValues",
            """[{"Key":"APP_ENV","Value":"production"},{"Key":"LOG_LEVEL","Value":"info"}]""");

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

        Add(action, "Squid.Action.KubernetesContainers.ConfigMapName", "my-config");
        Add(action, "Squid.Action.KubernetesContainers.ConfigMapValues",
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

        Add(action, "Squid.Action.KubernetesContainers.ConfigMapName", "my-config");
        Add(action, "Squid.Action.KubernetesContainers.ConfigMapValues",
            """{"KEY_A":"val-a","KEY_B":"val-b"}""");

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
