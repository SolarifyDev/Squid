using System.Collections.Generic;
using System.Text;
using System.Threading;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesIngressActionYamlGeneratorTests
{
    private readonly KubernetesIngressActionYamlGenerator _generator = new();

    // === CanHandle ===

    [Fact]
    public void CanHandle_CorrectActionType_ReturnsTrue()
    {
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployIngress" };
        _generator.CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_CaseInsensitive_ReturnsTrue()
    {
        var action = new DeploymentActionDto { ActionType = "squid.kubernetesdeployingress" };
        _generator.CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_WrongActionType_ReturnsFalse()
    {
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers" };
        _generator.CanHandle(action).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NullAction_ReturnsFalse()
    {
        _generator.CanHandle(null).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NullActionType_ReturnsFalse()
    {
        var action = new DeploymentActionDto { ActionType = null };
        _generator.CanHandle(action).ShouldBeFalse();
    }

    // === GenerateAsync — wrong action type returns empty ===

    [Fact]
    public async Task GenerateAsync_WrongActionType_ReturnsEmptyDictionary()
    {
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesRunScript" };
        var step = new DeploymentStepDto();

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    // === GenerateAsync — no rules returns empty ===

    [Fact]
    public async Task GenerateAsync_NoRulesProperty_ReturnsEmptyDictionary()
    {
        var (step, action) = CreateAction();

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GenerateAsync_EmptyRulesProperty_ReturnsEmptyDictionary()
    {
        var (step, action) = CreateAction(rules: "");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GenerateAsync_WhitespaceRulesProperty_ReturnsEmptyDictionary()
    {
        var (step, action) = CreateAction(rules: "   ");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    // === GenerateAsync — basic YAML structure ===

    [Fact]
    public async Task GenerateAsync_WithRules_ReturnsIngressYamlKey()
    {
        var (step, action) = CreateAction(rules: "[{\"host\":\"example.com\"}]");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldContainKey("ingress.yaml");
    }

    [Fact]
    public async Task GenerateAsync_WithRules_YamlContainsApiVersion()
    {
        var (step, action) = CreateAction(rules: "[{\"host\":\"example.com\"}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("apiVersion: networking.k8s.io/v1");
    }

    [Fact]
    public async Task GenerateAsync_WithRules_YamlContainsKindIngress()
    {
        var (step, action) = CreateAction(rules: "[{\"host\":\"example.com\"}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("kind: Ingress");
    }

    [Fact]
    public async Task GenerateAsync_WithRules_YamlContainsRules()
    {
        var (step, action) = CreateAction(rules: "[{\"host\":\"example.com\"}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("rules:");
        yaml.ShouldContain("- host: \"example.com\"");
    }

    // === GenerateAsync — ingress name ===

    [Fact]
    public async Task GenerateAsync_WithIngressName_YamlContainsName()
    {
        var (step, action) = CreateAction(
            ingressName: "my-ingress",
            rules: "[{\"host\":\"example.com\"}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("name: \"my-ingress\"");
    }

    [Fact]
    public async Task GenerateAsync_NoIngressName_DefaultsToIngress()
    {
        var (step, action) = CreateAction(rules: "[{\"host\":\"example.com\"}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("name: \"ingress\"");
    }

    [Fact]
    public async Task GenerateAsync_EmptyIngressName_DefaultsToIngress()
    {
        var (step, action) = CreateAction(
            ingressName: "",
            rules: "[{\"host\":\"example.com\"}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("name: \"ingress\"");
    }

    // === GenerateAsync — namespace ===

    [Fact]
    public async Task GenerateAsync_WithNamespace_YamlContainsNamespace()
    {
        var (step, action) = CreateAction(
            ns: "production",
            rules: "[{\"host\":\"example.com\"}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("namespace: \"production\"");
    }

    [Fact]
    public async Task GenerateAsync_NoNamespace_DefaultsToDefault()
    {
        var (step, action) = CreateAction(rules: "[{\"host\":\"example.com\"}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("namespace: \"default\"");
    }

    // === GenerateAsync — annotations ===

    [Fact]
    public async Task GenerateAsync_WithAnnotations_YamlContainsAnnotations()
    {
        var (step, action) = CreateAction(
            annotations: "[{\"Key\":\"nginx.ingress.kubernetes.io/rewrite-target\",\"Value\":\"/\"}]",
            rules: "[{\"host\":\"example.com\"}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("annotations:");
        yaml.ShouldContain("\"nginx.ingress.kubernetes.io/rewrite-target\": \"/\"");
    }

    [Fact]
    public async Task GenerateAsync_NoAnnotations_YamlDoesNotContainAnnotations()
    {
        var (step, action) = CreateAction(rules: "[{\"host\":\"example.com\"}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldNotContain("annotations:");
    }

    // === GenerateAsync — ingressClassName ===

    [Fact]
    public async Task GenerateAsync_WithIngressClassName_YamlContainsClassName()
    {
        var (step, action) = CreateAction(
            ingressClassName: "nginx",
            rules: "[{\"host\":\"example.com\"}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("ingressClassName: \"nginx\"");
    }

    [Fact]
    public async Task GenerateAsync_NoIngressClassName_YamlDoesNotContainClassName()
    {
        var (step, action) = CreateAction(rules: "[{\"host\":\"example.com\"}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldNotContain("ingressClassName:");
    }

    // === GenerateAsync — TLS ===

    [Fact]
    public async Task GenerateAsync_WithTls_YamlContainsTls()
    {
        var (step, action) = CreateAction(
            rules: "[{\"host\":\"example.com\"}]",
            tls: "[{\"secretName\":\"my-tls-secret\",\"hosts\":[\"example.com\"]}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("tls:");
        yaml.ShouldContain("secretName: \"my-tls-secret\"");
        yaml.ShouldContain("- \"example.com\"");
    }

    [Fact]
    public async Task GenerateAsync_NoTls_YamlDoesNotContainTls()
    {
        var (step, action) = CreateAction(rules: "[{\"host\":\"example.com\"}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldNotContain("tls:");
    }

    // === GenerateAsync — null properties ===

    [Fact]
    public async Task GenerateAsync_NullProperties_ReturnsEmptyDictionary()
    {
        var action = new DeploymentActionDto
        {
            ActionType = "Squid.KubernetesDeployIngress",
            Properties = null
        };
        var step = new DeploymentStepDto();

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    // === GenerateAsync — full scenario ===

    [Fact]
    public async Task GenerateAsync_FullScenario_YamlContainsAllSections()
    {
        var (step, action) = CreateAction(
            ingressName: "web-ingress",
            ns: "staging",
            annotations: "[{\"Key\":\"cert-manager.io/cluster-issuer\",\"Value\":\"letsencrypt\"}]",
            ingressClassName: "nginx",
            rules: "[{\"host\":\"app.example.com\",\"paths\":[{\"path\":\"/\",\"pathType\":\"Prefix\",\"backend\":{\"serviceName\":\"web-svc\",\"servicePort\":\"80\"}}]}]",
            tls: "[{\"secretName\":\"app-tls\",\"hosts\":[\"app.example.com\"]}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("apiVersion: networking.k8s.io/v1");
        yaml.ShouldContain("kind: Ingress");
        yaml.ShouldContain("name: \"web-ingress\"");
        yaml.ShouldContain("namespace: \"staging\"");
        yaml.ShouldContain("annotations:");
        yaml.ShouldContain("\"cert-manager.io/cluster-issuer\": \"letsencrypt\"");
        yaml.ShouldContain("ingressClassName: \"nginx\"");
        yaml.ShouldContain("rules:");
        yaml.ShouldContain("- host: \"app.example.com\"");
        yaml.ShouldContain("service:");
        yaml.ShouldContain("name: \"web-svc\"");
        yaml.ShouldContain("number: 80");
        yaml.ShouldContain("tls:");
        yaml.ShouldContain("secretName: \"app-tls\"");
    }

    // === GenerateAsync — rules with frontend flat format ===

    [Fact]
    public async Task GenerateAsync_Rules_FrontendFlatFormat_GeneratesK8sv1Backend()
    {
        var (step, action) = CreateAction(
            rules: "[{\"host\":\"api.example.com\",\"paths\":[{\"path\":\"/api\",\"pathType\":\"Prefix\",\"backend\":{\"serviceName\":\"api-svc\",\"servicePort\":\"8080\"}}]}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("- host: \"api.example.com\"");
        yaml.ShouldContain("http:");
        yaml.ShouldContain("paths:");
        yaml.ShouldContain("- path: \"/api\"");
        yaml.ShouldContain("pathType: \"Prefix\"");
        yaml.ShouldContain("backend:");
        yaml.ShouldContain("service:");
        yaml.ShouldContain("name: \"api-svc\"");
        yaml.ShouldContain("port:");
        yaml.ShouldContain("number: 8080");
    }

    [Fact]
    public async Task GenerateAsync_Rules_K8sv1Format_AcceptedAsBackwardCompat()
    {
        var (step, action) = CreateAction(
            rules: "[{\"host\":\"app.example.com\",\"http\":{\"paths\":[{\"path\":\"/\",\"pathType\":\"Prefix\",\"backend\":{\"service\":{\"name\":\"web-svc\",\"port\":{\"number\":80}}}}]}}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("- host: \"app.example.com\"");
        yaml.ShouldContain("name: \"web-svc\"");
        yaml.ShouldContain("number: 80");
    }

    // === GenerateAsync — flat path format (serviceName/servicePort at path level) ===

    [Fact]
    public async Task GenerateAsync_Rules_FlatPathServiceName_GeneratesBackend()
    {
        var (step, action) = CreateAction(
            rules: "[{\"host\":\"app.example.com\",\"paths\":[{\"path\":\"/\",\"pathType\":\"Prefix\",\"serviceName\":\"web-svc\",\"servicePort\":80}]}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("- host: \"app.example.com\"");
        yaml.ShouldContain("service:");
        yaml.ShouldContain("name: \"web-svc\"");
        yaml.ShouldContain("number: 80");
    }

    [Fact]
    public async Task GenerateAsync_Rules_IntegerServicePort_GeneratesPortNumber()
    {
        var (step, action) = CreateAction(
            rules: "[{\"host\":\"example.com\",\"paths\":[{\"path\":\"/\",\"pathType\":\"Prefix\",\"backend\":{\"serviceName\":\"my-svc\",\"servicePort\":443}}]}]");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("number: 443");
    }

    // === GenerateAsync — only one file returned ===

    [Fact]
    public async Task GenerateAsync_WithRules_ReturnsExactlyOneFile()
    {
        var (step, action) = CreateAction(rules: "[{\"host\":\"example.com\"}]");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);

        result.Count.ShouldBe(1);
    }

    // === GenerateAsync — output is UTF8 ===

    [Fact]
    public async Task GenerateAsync_OutputIsValidUtf8()
    {
        var (step, action) = CreateAction(rules: "[{\"host\":\"example.com\"}]");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var bytes = result["ingress.yaml"];
        var decoded = Encoding.UTF8.GetString(bytes);

        decoded.ShouldContain("apiVersion:");
    }

    // === Helpers ===

    private async Task<string> GetIngressYaml(DeploymentStepDto step, DeploymentActionDto action)
    {
        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        return Encoding.UTF8.GetString(result["ingress.yaml"]);
    }

    private static (DeploymentStepDto step, DeploymentActionDto action) CreateAction(
        string ingressName = null,
        string ns = null,
        string annotations = null,
        string ingressClassName = null,
        string rules = null,
        string tls = null)
    {
        var properties = new List<DeploymentActionPropertyDto>();

        if (ingressName != null)
            properties.Add(Prop("Squid.Action.KubernetesContainers.IngressName", ingressName));
        if (ns != null)
            properties.Add(Prop("Squid.Action.Kubernetes.Namespace", ns));
        if (annotations != null)
            properties.Add(Prop("Squid.Action.KubernetesContainers.IngressAnnotations", annotations));
        if (ingressClassName != null)
            properties.Add(Prop("Squid.Action.KubernetesContainers.IngressClassName", ingressClassName));
        if (rules != null)
            properties.Add(Prop("Squid.Action.KubernetesContainers.IngressRules", rules));
        if (tls != null)
            properties.Add(Prop("Squid.Action.KubernetesContainers.IngressTlsCertificates", tls));

        var action = new DeploymentActionDto
        {
            ActionType = "Squid.KubernetesDeployIngress",
            Properties = properties
        };

        var step = new DeploymentStepDto();

        return (step, action);
    }

    private static DeploymentActionPropertyDto Prop(string name, string value) =>
        new() { PropertyName = name, PropertyValue = value };
}
