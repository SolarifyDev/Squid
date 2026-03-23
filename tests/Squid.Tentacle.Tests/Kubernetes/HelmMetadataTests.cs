using System.Collections.Generic;
using k8s.Models;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

public class HelmMetadataTests
{
    [Fact]
    public void ApplyHelmAnnotations_WithReleaseName_AddsAnnotations()
    {
        var metadata = new V1ObjectMeta { Name = "test-resource" };
        var settings = new KubernetesSettings { ReleaseName = "my-release", TentacleNamespace = "squid-ns" };

        HelmMetadata.ApplyHelmAnnotations(metadata, settings);

        metadata.Annotations.ShouldContainKeyAndValue("meta.helm.sh/release-name", "my-release");
        metadata.Annotations.ShouldContainKeyAndValue("meta.helm.sh/release-namespace", "squid-ns");
    }

    [Fact]
    public void ApplyHelmAnnotations_EmptyReleaseName_NoAnnotations()
    {
        var metadata = new V1ObjectMeta { Name = "test-resource" };
        var settings = new KubernetesSettings { ReleaseName = "" };

        HelmMetadata.ApplyHelmAnnotations(metadata, settings);

        metadata.Annotations.ShouldBeNull();
    }

    [Fact]
    public void ApplyHelmAnnotations_PreservesExistingAnnotations()
    {
        var metadata = new V1ObjectMeta
        {
            Name = "test-resource",
            Annotations = new Dictionary<string, string> { ["existing-key"] = "existing-value" }
        };
        var settings = new KubernetesSettings { ReleaseName = "my-release", TentacleNamespace = "squid-ns" };

        HelmMetadata.ApplyHelmAnnotations(metadata, settings);

        metadata.Annotations.ShouldContainKeyAndValue("existing-key", "existing-value");
        metadata.Annotations.ShouldContainKeyAndValue("meta.helm.sh/release-name", "my-release");
        metadata.Annotations.ShouldContainKeyAndValue("meta.helm.sh/release-namespace", "squid-ns");
    }
}
