using System.Text;
using System.Threading;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class IngressResourceGeneratorTests
{
    private readonly KubernetesContainersActionYamlGenerator _compositor = new();

    // === Kind / apiVersion ===

    [Fact]
    public async Task Generate_BasicIngress_HasCorrectApiVersionAndKind()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("apiVersion: networking.k8s.io/v1");
        yaml.ShouldContain("kind: Ingress");
    }

    // === Metadata: name ===

    [Fact]
    public async Task Generate_CustomIngressName_UsedInMetadata()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.IngressName", "my-ingress");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("name: my-ingress");
    }

    [Fact]
    public async Task Generate_NoIngressName_DefaultNameUsed()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("name: ingress");
    }

    [Fact]
    public async Task Generate_IngressNamespace_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.Namespace", "web-ns");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("namespace: web-ns");
    }

    [Fact]
    public async Task Generate_IngressWithAnnotations_IncludesAnnotationsBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.IngressAnnotations",
            """[{"Key":"nginx.ingress.kubernetes.io/rewrite-target","Value":"/"}]""");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("annotations:");
        yaml.ShouldContain("nginx.ingress.kubernetes.io/rewrite-target: /");
    }

    // === Spec: ingressClassName ===

    [Fact]
    public async Task Generate_IngressClassName_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.IngressClassName", "nginx");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("ingressClassName: nginx");
    }

    [Fact]
    public async Task Generate_NoIngressClassName_IngressClassNameOmitted()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldNotContain("ingressClassName:");
    }

    // === Rules ===

    [Fact]
    public async Task Generate_Rules_IncludesRulesBlock()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("rules:");
    }

    [Fact]
    public async Task Generate_RuleWithHost_HostIncluded()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("host: example.com");
    }

    [Fact]
    public async Task Generate_RulePaths_HttpAndPathsIncluded()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("http:");
        yaml.ShouldContain("paths:");
        yaml.ShouldContain("path: /");
        yaml.ShouldContain("pathType: Prefix");
    }

    [Fact]
    public async Task Generate_MultipleRules_AllHostsGenerated()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.IngressRules");
        Add(action, "Squid.Action.KubernetesContainers.IngressRules",
            """[{"host":"api.example.com","paths":[{"path":"/api","pathType":"Prefix","backend":{"serviceName":"api-svc","servicePort":"80"}}]},{"host":"web.example.com","paths":[{"path":"/","pathType":"Prefix","backend":{"serviceName":"web-svc","servicePort":"80"}}]}]""");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("host: api.example.com");
        yaml.ShouldContain("host: web.example.com");
    }

    [Fact]
    public async Task Generate_MultiplePathsInRule_AllPathsGenerated()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.IngressRules");
        Add(action, "Squid.Action.KubernetesContainers.IngressRules",
            """[{"host":"example.com","paths":[{"path":"/api","pathType":"Exact","backend":{"serviceName":"api-svc","servicePort":"8080"}},{"path":"/web","pathType":"Prefix","backend":{"serviceName":"web-svc","servicePort":"80"}}]}]""");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("path: /api");
        yaml.ShouldContain("pathType: Exact");
        yaml.ShouldContain("path: /web");
        yaml.ShouldContain("pathType: Prefix");
    }

    // === Backend formats ===

    [Fact]
    public async Task Generate_FlatBackendFormat_GeneratesServiceRef()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("backend:");
        yaml.ShouldContain("service:");
        yaml.ShouldContain("name: my-service");
        yaml.ShouldContain("number: 80");
    }

    [Fact]
    public async Task Generate_FlatBackendWithStringPort_PortAsNumber()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.IngressRules");
        Add(action, "Squid.Action.KubernetesContainers.IngressRules",
            """[{"host":"example.com","paths":[{"path":"/","pathType":"Prefix","backend":{"serviceName":"my-svc","servicePort":"8080"}}]}]""");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("number: 8080");
    }

    [Fact]
    public async Task Generate_K8sV1BackendFormat_GeneratesServiceRef()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.IngressRules");
        Add(action, "Squid.Action.KubernetesContainers.IngressRules",
            """[{"host":"example.com","http":{"paths":[{"path":"/","pathType":"Prefix","backend":{"service":{"name":"k8s-svc","port":{"number":9090}}}}]}}]""");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("name: k8s-svc");
        yaml.ShouldContain("number: 9090");
    }

    // === TLS ===

    [Fact]
    public async Task Generate_TlsCertificate_IncludesTlsBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.IngressTlsCertificates",
            """[{"hosts":["example.com"],"secretName":"tls-secret"}]""");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("tls:");
        yaml.ShouldContain("- hosts:");
        yaml.ShouldContain("- example.com");
        yaml.ShouldContain("secretName: tls-secret");
    }

    [Fact]
    public async Task Generate_TlsMultipleHosts_AllHostsIncluded()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.IngressTlsCertificates",
            """[{"hosts":["api.example.com","web.example.com"],"secretName":"wildcard-tls"}]""");

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldContain("- api.example.com");
        yaml.ShouldContain("- web.example.com");
        yaml.ShouldContain("secretName: wildcard-tls");
    }

    [Fact]
    public async Task Generate_NoTls_TlsBlockOmitted()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetIngressYaml(step, action);

        yaml.ShouldNotContain("tls:");
    }

    // === CanGenerate guards ===

    [Fact]
    public async Task Generate_NoIngressRules_IngressYamlNotGenerated()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers", Name = "test" };

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldNotContainKey("ingress.yaml");
    }

    [Fact]
    public async Task Generate_EmptyIngressRulesArray_IngressYamlNotGenerated()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers", Name = "test" };
        Add(action, "Squid.Action.KubernetesContainers.IngressRules", "[]");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldNotContainKey("ingress.yaml");
    }

    // === Helpers ===

    private async Task<string> GetIngressYaml(DeploymentStepDto step, DeploymentActionDto action)
    {
        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        result.ShouldContainKey("ingress.yaml");
        return Encoding.UTF8.GetString(result["ingress.yaml"]);
    }

    private static (DeploymentStepDto step, DeploymentActionDto action) CreateMinimal()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto
        {
            ActionType = "Squid.KubernetesDeployContainers",
            Name = "test-deploy"
        };

        Add(action, "Squid.Action.KubernetesContainers.IngressRules",
            """[{"host":"example.com","paths":[{"path":"/","pathType":"Prefix","backend":{"serviceName":"my-service","servicePort":"80"}}]}]""");

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
