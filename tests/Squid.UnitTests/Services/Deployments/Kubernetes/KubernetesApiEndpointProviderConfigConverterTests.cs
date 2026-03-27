using System.Text.Json;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesApiEndpointProviderConfigConverterTests
{
    [Fact]
    public void Deserialize_AwsEks_ReturnsCorrectType()
    {
        var json = JsonSerializer.Serialize(new KubernetesApiAwsEksConfig { ClusterName = "my-cluster", Region = "us-west-2" });

        var result = KubernetesApiEndpointProviderConfigConverter.Deserialize(KubernetesApiEndpointProviderType.AwsEks, json);

        var aws = result.ShouldBeOfType<KubernetesApiAwsEksConfig>();
        aws.ClusterName.ShouldBe("my-cluster");
        aws.Region.ShouldBe("us-west-2");
    }

    [Fact]
    public void Deserialize_AzureAks_ReturnsCorrectType()
    {
        var json = JsonSerializer.Serialize(new KubernetesApiAzureAksConfig { ClusterName = "aks-cluster", ResourceGroup = "my-rg" });

        var result = KubernetesApiEndpointProviderConfigConverter.Deserialize(KubernetesApiEndpointProviderType.AzureAks, json);

        var azure = result.ShouldBeOfType<KubernetesApiAzureAksConfig>();
        azure.ClusterName.ShouldBe("aks-cluster");
        azure.ResourceGroup.ShouldBe("my-rg");
    }

    [Fact]
    public void Deserialize_GcpGke_ReturnsCorrectType()
    {
        var json = JsonSerializer.Serialize(new KubernetesApiGcpGkeConfig { ClusterName = "gke-cluster", Project = "my-project", Zone = "us-central1-a", Region = "us-central1", UseClusterInternalIp = "True" });

        var result = KubernetesApiEndpointProviderConfigConverter.Deserialize(KubernetesApiEndpointProviderType.GcpGke, json);

        var gcp = result.ShouldBeOfType<KubernetesApiGcpGkeConfig>();
        gcp.ClusterName.ShouldBe("gke-cluster");
        gcp.Project.ShouldBe("my-project");
        gcp.Zone.ShouldBe("us-central1-a");
        gcp.Region.ShouldBe("us-central1");
        gcp.UseClusterInternalIp.ShouldBe("True");
    }

    [Fact]
    public void Deserialize_None_ReturnsNull()
    {
        var json = JsonSerializer.Serialize(new { Foo = "bar" });

        var result = KubernetesApiEndpointProviderConfigConverter.Deserialize(KubernetesApiEndpointProviderType.None, json);

        result.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Deserialize_EmptyOrNullJson_ReturnsNull(string json)
    {
        var result = KubernetesApiEndpointProviderConfigConverter.Deserialize(KubernetesApiEndpointProviderType.AwsEks, json);

        result.ShouldBeNull();
    }

    [Fact]
    public void Serialize_RoundTrip_PreservesValues()
    {
        var original = new KubernetesApiAwsEksConfig { ClusterName = "test-cluster", Region = "eu-west-1" };

        var json = KubernetesApiEndpointProviderConfigConverter.Serialize(original);
        var restored = KubernetesApiEndpointProviderConfigConverter.Deserialize(KubernetesApiEndpointProviderType.AwsEks, json);

        var aws = restored.ShouldBeOfType<KubernetesApiAwsEksConfig>();
        aws.ClusterName.ShouldBe("test-cluster");
        aws.Region.ShouldBe("eu-west-1");
    }

    [Fact]
    public void Serialize_Null_ReturnsNull()
    {
        var result = KubernetesApiEndpointProviderConfigConverter.Serialize(null);

        result.ShouldBeNull();
    }

    // === camelCase JSON (frontend format) ===

    [Fact]
    public void Deserialize_AwsEks_CamelCaseJson_ReturnsCorrectValues()
    {
        const string json = """{"clusterName":"my-cluster","region":"ap-east-1","useInstanceRole":false}""";

        var result = KubernetesApiEndpointProviderConfigConverter.Deserialize(KubernetesApiEndpointProviderType.AwsEks, json);

        var aws = result.ShouldBeOfType<KubernetesApiAwsEksConfig>();
        aws.ClusterName.ShouldBe("my-cluster");
        aws.Region.ShouldBe("ap-east-1");
        aws.UseInstanceRole.ShouldBeFalse();
    }

    [Fact]
    public void Deserialize_AzureAks_CamelCaseJson_ReturnsCorrectValues()
    {
        const string json = """{"clusterName":"aks-cluster","resourceGroup":"my-rg"}""";

        var result = KubernetesApiEndpointProviderConfigConverter.Deserialize(KubernetesApiEndpointProviderType.AzureAks, json);

        var azure = result.ShouldBeOfType<KubernetesApiAzureAksConfig>();
        azure.ClusterName.ShouldBe("aks-cluster");
        azure.ResourceGroup.ShouldBe("my-rg");
    }

    [Fact]
    public void Deserialize_GcpGke_CamelCaseJson_ReturnsCorrectValues()
    {
        const string json = """{"clusterName":"gke","project":"p","zone":"z","region":"r","useClusterInternalIp":"True"}""";

        var result = KubernetesApiEndpointProviderConfigConverter.Deserialize(KubernetesApiEndpointProviderType.GcpGke, json);

        var gcp = result.ShouldBeOfType<KubernetesApiGcpGkeConfig>();
        gcp.ClusterName.ShouldBe("gke");
        gcp.Project.ShouldBe("p");
        gcp.Zone.ShouldBe("z");
        gcp.Region.ShouldBe("r");
        gcp.UseClusterInternalIp.ShouldBe("True");
    }
}
