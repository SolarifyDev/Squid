using Squid.Calamari.Kubernetes;

namespace Squid.Calamari.Tests.Calamari.Kubernetes;

public class KubernetesDeployIdAnnotatorTests
{
    [Theory]
    [InlineData("Deployment")]
    [InlineData("StatefulSet")]
    [InlineData("DaemonSet")]
    public void InjectDeployId_WorkloadKind_InjectsAnnotationIntoPodTemplate(string kind)
    {
        var yaml = $"""
            apiVersion: apps/v1
            kind: {kind}
            metadata:
              name: my-app
            spec:
              template:
                metadata:
                  labels:
                    app: my-app
                spec:
                  containers:
                  - name: app
                    image: nginx
            """;

        var result = KubernetesDeployIdAnnotator.InjectDeployId(yaml, "42");

        result.ShouldContain("squid.io/deploy-id: \"42\"");
    }

    [Theory]
    [InlineData("Service")]
    [InlineData("ConfigMap")]
    [InlineData("Secret")]
    [InlineData("Ingress")]
    [InlineData("Namespace")]
    public void InjectDeployId_NonWorkloadKind_ReturnsUnchanged(string kind)
    {
        var yaml = $"""
            apiVersion: v1
            kind: {kind}
            metadata:
              name: my-resource
            """;

        var result = KubernetesDeployIdAnnotator.InjectDeployId(yaml, "42");

        result.ShouldNotContain("squid.io/deploy-id");
    }

    [Fact]
    public void InjectDeployId_ExistingAnnotations_PreservesAndAddsDeployId()
    {
        var yaml = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: my-app
            spec:
              template:
                metadata:
                  annotations:
                    existing-key: existing-value
                spec:
                  containers:
                  - name: app
                    image: nginx
            """;

        var result = KubernetesDeployIdAnnotator.InjectDeployId(yaml, "99");

        result.ShouldContain("existing-key");
        result.ShouldContain("existing-value");
        result.ShouldContain("squid.io/deploy-id");
    }

    [Fact]
    public void InjectDeployId_NoAnnotationsNode_CreatesAnnotationsWithDeployId()
    {
        var yaml = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: my-app
            spec:
              template:
                metadata:
                  labels:
                    app: my-app
                spec:
                  containers:
                  - name: app
                    image: nginx
            """;

        var result = KubernetesDeployIdAnnotator.InjectDeployId(yaml, "7");

        result.ShouldContain("squid.io/deploy-id");
    }

    [Fact]
    public void InjectDeployId_NoPodTemplateMetadata_CreatesFullPath()
    {
        var yaml = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: my-app
            spec:
              template:
                spec:
                  containers:
                  - name: app
                    image: nginx
            """;

        var result = KubernetesDeployIdAnnotator.InjectDeployId(yaml, "5");

        result.ShouldContain("squid.io/deploy-id");
    }

    [Fact]
    public void InjectDeployId_MultiDocumentYaml_PatchesOnlyWorkloads()
    {
        var yaml = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: my-config
            data:
              key: value
            ---
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: my-app
            spec:
              template:
                metadata:
                  labels:
                    app: my-app
                spec:
                  containers:
                  - name: app
                    image: nginx
            """;

        var result = KubernetesDeployIdAnnotator.InjectDeployId(yaml, "123");

        var count = result.Split("squid.io/deploy-id").Length - 1;
        count.ShouldBe(1);
    }

    [Fact]
    public void InjectDeployId_ExistingDeployId_OverwritesWithNewValue()
    {
        var yaml = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: my-app
            spec:
              template:
                metadata:
                  annotations:
                    squid.io/deploy-id: '10'
                spec:
                  containers:
                  - name: app
                    image: nginx
            """;

        var result = KubernetesDeployIdAnnotator.InjectDeployId(yaml, "20");

        result.ShouldContain("squid.io/deploy-id");
        result.ShouldContain("20");
        result.ShouldNotContain("'10'");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void InjectDeployId_EmptyOrNullDeployId_ReturnsUnchanged(string deployId)
    {
        var yaml = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: my-app
            spec:
              template:
                spec:
                  containers:
                  - name: app
                    image: nginx
            """;

        var result = KubernetesDeployIdAnnotator.InjectDeployId(yaml, deployId);

        result.ShouldNotContain("squid.io/deploy-id");
    }

    [Fact]
    public void InjectDeployId_InvalidYaml_ReturnsUnchanged()
    {
        var yaml = "this is: [not: valid: yaml: {{{}}}";

        var result = KubernetesDeployIdAnnotator.InjectDeployId(yaml, "42");

        result.ShouldBe(yaml);
    }

    [Fact]
    public void InjectDeployId_KindCaseInsensitive_Matches()
    {
        var yaml = """
            apiVersion: apps/v1
            kind: deployment
            metadata:
              name: my-app
            spec:
              template:
                spec:
                  containers:
                  - name: app
                    image: nginx
            """;

        var result = KubernetesDeployIdAnnotator.InjectDeployId(yaml, "55");

        result.ShouldContain("squid.io/deploy-id");
    }

    [Fact]
    public void InjectDeployId_MultipleWorkloads_PatchesAll()
    {
        var yaml = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: web
            spec:
              template:
                metadata:
                  labels:
                    app: web
                spec:
                  containers:
                  - name: web
                    image: nginx
            ---
            apiVersion: apps/v1
            kind: StatefulSet
            metadata:
              name: db
            spec:
              template:
                metadata:
                  labels:
                    app: db
                spec:
                  containers:
                  - name: db
                    image: postgres
            """;

        var result = KubernetesDeployIdAnnotator.InjectDeployId(yaml, "77");

        var count = result.Split("squid.io/deploy-id").Length - 1;
        count.ShouldBe(2);
    }
}
