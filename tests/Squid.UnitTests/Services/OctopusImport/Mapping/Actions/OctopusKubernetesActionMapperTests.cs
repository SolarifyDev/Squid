using System.Linq;
using System.Text.Json;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.OctopusImport.Mapping.Actions;

public class OctopusKubernetesActionMapperTests
{
    [Fact]
    public void ContainersMapper_MapsCorePropertiesAndNormalizesPackageReferences()
    {
        var mapper = new OctopusKubernetesDeployContainersActionMapper();
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-Containers",
            Name = "Deploy containers",
            ActionType = "Octopus.KubernetesDeployContainers",
            IsRequired = true,
            Packages =
            [
                new OctopusActionPackageDto
                {
                    Id = "Packages-1",
                    Name = "web",
                    PackageId = "sjdistributor/web",
                    FeedId = "Feeds-1083-D4C63A85E7894C5D8C20D9297FEA1A43"
                }
            ],
            Properties =
            {
                ["Octopus.Action.KubernetesContainers.DeploymentName"] = "deploy-web",
                ["Octopus.Action.KubernetesContainers.Namespace"] = "#{K8SNamespace}",
                ["Octopus.Action.KubernetesContainers.Replicas"] = "#{NumOfPod}",
                ["Octopus.Action.KubernetesContainers.ServiceName"] = "web-service",
                ["Octopus.Action.KubernetesContainers.ServiceType"] = "ClusterIP",
                ["Octopus.Action.KubernetesContainers.ServicePorts"] = """[{"name":"http","port":"3000","targetPort":"3000","protocol":"TCP"}]""",
                ["Octopus.Action.KubernetesContainers.ConfigMapName"] = "web-config",
                ["Octopus.Action.KubernetesContainers.ConfigMapValues"] = """{"OPENAI_API_KEY":"#{OPENAI_API_KEY}","BASE_URL":"#{BASE_URL}"}""",
                ["Octopus.Action.KubernetesContainers.SecretName"] = "web-secret",
                ["Octopus.Action.KubernetesContainers.SecretValues"] = """[{"key":"TOKEN","value":"#{TOKEN}","valueError":null}]""",
                ["Octopus.Action.KubernetesContainers.Containers"] = """
                [{
                  "Name":"web",
                  "FeedId":"Feeds-1083",
                  "Ports":[{"key":"http","value":"3000","option":"TCP"}],
                  "Resources":{"requests":{"cpu":"#{CpuRequest}","memory":"128Mi"},"limits":{"cpu":"#{CpuLimit}","memory":"256Mi"}},
                  "CreateFeedSecrets":"True"
                }]
                """,
                ["Octopus.Action.Kubernetes.ResourceStatusCheck"] = "True",
                ["Octopus.Action.Kubernetes.DeploymentTimeout"] = "180",
                ["Octopus.Action.Kubernetes.ServerSideApply.Enabled"] = "True",
                ["Octopus.Action.Kubernetes.ServerSideApply.ForceConflicts"] = "True"
            }
        };

        var result = mapper.Map(action, ContextWithFeedMapping());

        result.HasBlockers.ShouldBeFalse();
        result.Action.Name.ShouldBe("Deploy containers");
        result.Action.ActionType.ShouldBe(SpecialVariables.ActionTypes.KubernetesDeployContainers);
        result.Action.IsRequired.ShouldBeTrue();
        Property(result.Action, "Squid.Action.KubernetesContainers.Namespace").ShouldBe("#{K8SNamespace}");
        Property(result.Action, "Squid.Action.KubernetesContainers.Replicas").ShouldBe("#{NumOfPod}");
        Property(result.Action, "Squid.Action.KubernetesContainers.ServiceName").ShouldBe("web-service");
        Property(result.Action, "Squid.Action.KubernetesContainers.ConfigMapValues").ShouldContain("OPENAI_API_KEY");
        Property(result.Action, "Squid.Action.KubernetesContainers.SecretValues").ShouldContain("TOKEN");
        Property(result.Action, "Squid.Action.KubernetesContainers.ObjectStatusCheck").ShouldBe("True");
        Property(result.Action, "Squid.Action.KubernetesContainers.ObjectStatusCheckTimeout").ShouldBe("180");
        Property(result.Action, "Squid.Action.Kubernetes.ServerSideApply.Enabled").ShouldBe("True");

        using var containers = JsonDocument.Parse(Property(result.Action, "Squid.Action.KubernetesContainers.Containers"));
        var container = containers.RootElement.EnumerateArray().Single();
        container.GetProperty("Name").GetString().ShouldBe("web");
        container.GetProperty("PackageId").GetString().ShouldBe("sjdistributor/web");
        container.GetProperty("FeedId").GetInt32().ShouldBe(77);
        container.GetProperty("CreateFeedSecrets").GetString().ShouldBe("True");
        container.GetProperty("Resources").GetProperty("requests").GetProperty("cpu").GetString().ShouldBe("#{CpuRequest}");
    }

    [Fact]
    public void ContainersMapper_WhenFeedCannotBeMapped_AddsBlocker()
    {
        var mapper = new OctopusKubernetesDeployContainersActionMapper();
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-Containers",
            Name = "Deploy containers",
            ActionType = "Octopus.KubernetesDeployContainers",
            Properties =
            {
                ["Octopus.Action.KubernetesContainers.Containers"] = """[{"Name":"web","FeedId":"Feeds-Missing","PackageId":"web"}]"""
            }
        };

        var result = mapper.Map(action, new OctopusImportActionMappingContext(new OctopusImportIdMap(), 42));

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.ShouldContain(d => d.Code == OctopusImportActionMappingDiagnosticCodes.MissingFeedMapping);
    }

    [Fact]
    public void IngressMapper_MapsAnnotationsRulesClassNamespaceAndTls()
    {
        var mapper = new OctopusKubernetesDeployIngressActionMapper();
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-Ingress",
            Name = "Deploy ingress",
            ActionType = "Octopus.KubernetesDeployIngress",
            Properties =
            {
                ["Octopus.Action.KubernetesContainers.IngressName"] = "ingress-web",
                ["Octopus.Action.KubernetesContainers.IngressClassName"] = "nginx",
                ["Octopus.Action.KubernetesContainers.Namespace"] = "#{K8SNamespace}",
                ["Octopus.Action.KubernetesContainers.IngressAnnotations"] = """[{"key":"cert-manager.io/cluster-issuer","value":"letsencrypt","keyError":null}]""",
                ["Octopus.Action.KubernetesContainers.IngressRules"] = """[{"host":"#{IngressDomainName}","http":{"paths":[{"key":"/","value":"3000","option":"web-service","option2":"ImplementationSpecific"}]}}]""",
                ["Octopus.Action.KubernetesContainers.IngressTlsCertificates"] = """[{"hosts":["#{IngressDomainName}"],"certificateVariableName":null,"secretName":"#{TlsSecret}"}]""",
                ["Octopus.Action.Kubernetes.ResourceStatusCheck"] = "True",
                ["Octopus.Action.Kubernetes.DeploymentTimeout"] = "180"
            }
        };

        var result = mapper.Map(action, new OctopusImportActionMappingContext(new OctopusImportIdMap(), 42));

        result.HasBlockers.ShouldBeFalse();
        result.Action.ActionType.ShouldBe(SpecialVariables.ActionTypes.KubernetesDeployIngress);
        Property(result.Action, "Squid.Action.KubernetesContainers.IngressName").ShouldBe("ingress-web");
        Property(result.Action, "Squid.Action.KubernetesContainers.IngressClassName").ShouldBe("nginx");
        Property(result.Action, "Squid.Action.KubernetesContainers.Namespace").ShouldBe("#{K8SNamespace}");
        Property(result.Action, "Squid.Action.KubernetesContainers.ObjectStatusCheck").ShouldBe("True");

        using var annotations = JsonDocument.Parse(Property(result.Action, "Squid.Action.KubernetesContainers.IngressAnnotations"));
        annotations.RootElement.EnumerateArray().Single().GetProperty("Key").GetString().ShouldBe("cert-manager.io/cluster-issuer");

        using var rules = JsonDocument.Parse(Property(result.Action, "Squid.Action.KubernetesContainers.IngressRules"));
        var path = rules.RootElement.EnumerateArray().Single().GetProperty("paths").EnumerateArray().Single();
        path.GetProperty("path").GetString().ShouldBe("/");
        path.GetProperty("pathType").GetString().ShouldBe("ImplementationSpecific");
        path.GetProperty("backend").GetProperty("serviceName").GetString().ShouldBe("web-service");
        path.GetProperty("backend").GetProperty("servicePort").GetString().ShouldBe("3000");

        using var tls = JsonDocument.Parse(Property(result.Action, "Squid.Action.KubernetesContainers.IngressTlsCertificates"));
        var tlsEntry = tls.RootElement.EnumerateArray().Single();
        tlsEntry.GetProperty("secretName").GetString().ShouldBe("#{TlsSecret}");
        tlsEntry.GetProperty("hosts").EnumerateArray().Single().GetString().ShouldBe("#{IngressDomainName}");
        tlsEntry.TryGetProperty("certificateVariableName", out _).ShouldBeFalse();
    }

    private static OctopusImportActionMappingContext ContextWithFeedMapping()
    {
        var idMap = new OctopusImportIdMap();
        idMap.AddReused(
            Resource("Feeds-1083-D4C63A85E7894C5D8C20D9297FEA1A43", OctopusResourceKind.Feed, "Docker Hub", new OctopusFeedDto()),
            77);

        return new OctopusImportActionMappingContext(idMap, 42);
    }

    private static string Property(CreateOrUpdateDeploymentActionModel action, string name)
        => action.Properties.Single(p => p.PropertyName == name).PropertyValue;

    private static OctopusResourceNode Resource(
        string sourceId,
        OctopusResourceKind kind,
        string name,
        object source)
        => new(
            sourceId,
            name,
            kind,
            OctopusDocumentKind.DeploymentProcess,
            $"{sourceId}.json",
            null,
            null,
            false,
            source);
}
