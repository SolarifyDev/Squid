using Squid.Calamari.Kubernetes;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Tests.Calamari.Kubernetes;

public class TokenSubstitutingYamlManifestRendererTests
{
    [Fact]
    public async Task RenderAsync_SingleFile_ReplacesTokens_AndWritesExpandedFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-renderer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var yamlPath = Path.Combine(tempDir, "input.yaml");
            File.WriteAllText(yamlPath, "name: #{App}\nns: #{Namespace}\n");

            var variables = new VariableSet();
            variables.Set("App", "squid");
            variables.Set("Namespace", "dev");

            var renderer = new TokenSubstitutingYamlManifestRenderer();
            var trackedTemps = new List<string>();
            var rendered = await renderer.RenderAsync(
                new KubernetesApplyRequest
                {
                    WorkingDirectory = tempDir,
                    YamlFilePath = yamlPath,
                    Variables = variables,
                    Namespace = "ignored",
                    TemporaryFiles = trackedTemps
                },
                new ResolvedKubernetesManifestSource
                {
                    ManifestRootDirectory = tempDir,
                    ManifestFilePaths = [yamlPath]
                },
                CancellationToken.None);

            rendered.Recursive.ShouldBeFalse();
            rendered.ApplyPath.ShouldStartWith(Path.Combine(tempDir, ".squid-expanded-"));
            rendered.ApplyPath.ShouldEndWith("-input.yaml");
            File.ReadAllText(rendered.ApplyPath).ShouldBe("name: squid\nns: dev\n");
            trackedTemps.ShouldContain(rendered.ApplyPath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RenderAsync_MultiFile_RendersManifestSetDirectory_PreservingRelativePaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-renderer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var sourceDir = Path.Combine(tempDir, "source");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(Path.Combine(sourceDir, "nested"));

            var fileA = Path.Combine(sourceDir, "a.yaml");
            var fileB = Path.Combine(sourceDir, "nested", "b.yml");
            File.WriteAllText(fileA, "name: #{App}\n");
            File.WriteAllText(fileB, "ns: #{Namespace}\n");

            var variables = new VariableSet();
            variables.Set("App", "squid");
            variables.Set("Namespace", "prod");

            var trackedTemps = new List<string>();
            var renderer = new TokenSubstitutingYamlManifestRenderer();

            var rendered = await renderer.RenderAsync(
                new KubernetesApplyRequest
                {
                    WorkingDirectory = tempDir,
                    YamlFilePath = sourceDir,
                    Variables = variables,
                    TemporaryFiles = trackedTemps
                },
                new ResolvedKubernetesManifestSource
                {
                    ManifestRootDirectory = sourceDir,
                    ManifestFilePaths = [fileA, fileB]
                },
                CancellationToken.None);

            rendered.Recursive.ShouldBeTrue();
            Directory.Exists(rendered.ApplyPath).ShouldBeTrue();
            rendered.ApplyPath.ShouldStartWith(Path.Combine(tempDir, ".squid-expanded-manifests-"));
            trackedTemps.ShouldContain(rendered.ApplyPath);

            File.ReadAllText(Path.Combine(rendered.ApplyPath, "a.yaml")).ShouldBe("name: squid\n");
            File.ReadAllText(Path.Combine(rendered.ApplyPath, "nested", "b.yml")).ShouldBe("ns: prod\n");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RenderAsync_SingleFile_DeploymentWithDeployId_InjectsAnnotation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-renderer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var yamlContent = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: my-app\nspec:\n  template:\n    metadata:\n      labels:\n        app: my-app\n    spec:\n      containers:\n      - name: app\n        image: nginx\n";
            var yamlPath = Path.Combine(tempDir, "deploy.yaml");
            File.WriteAllText(yamlPath, yamlContent);

            var variables = new VariableSet();
            variables.Set("Squid.Deployment.Id", "42");

            var renderer = new TokenSubstitutingYamlManifestRenderer();
            var rendered = await renderer.RenderAsync(
                new KubernetesApplyRequest
                {
                    WorkingDirectory = tempDir,
                    YamlFilePath = yamlPath,
                    Variables = variables,
                    TemporaryFiles = new List<string>()
                },
                new ResolvedKubernetesManifestSource
                {
                    ManifestRootDirectory = tempDir,
                    ManifestFilePaths = [yamlPath]
                },
                CancellationToken.None);

            var output = File.ReadAllText(rendered.ApplyPath);
            output.ShouldContain("squid.io/deploy-id");
            output.ShouldContain("42");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RenderAsync_SingleFile_NoDeployIdVariable_DoesNotInjectAnnotation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-renderer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var yamlContent = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: my-app\nspec:\n  template:\n    spec:\n      containers:\n      - name: app\n        image: nginx\n";
            var yamlPath = Path.Combine(tempDir, "deploy.yaml");
            File.WriteAllText(yamlPath, yamlContent);

            var variables = new VariableSet();

            var renderer = new TokenSubstitutingYamlManifestRenderer();
            var rendered = await renderer.RenderAsync(
                new KubernetesApplyRequest
                {
                    WorkingDirectory = tempDir,
                    YamlFilePath = yamlPath,
                    Variables = variables,
                    TemporaryFiles = new List<string>()
                },
                new ResolvedKubernetesManifestSource
                {
                    ManifestRootDirectory = tempDir,
                    ManifestFilePaths = [yamlPath]
                },
                CancellationToken.None);

            var output = File.ReadAllText(rendered.ApplyPath);
            output.ShouldNotContain("squid.io/deploy-id");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RenderAsync_SingleFile_NonWorkloadKind_DoesNotInjectAnnotation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-renderer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var yamlContent = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: my-config\ndata:\n  key: value\n";
            var yamlPath = Path.Combine(tempDir, "configmap.yaml");
            File.WriteAllText(yamlPath, yamlContent);

            var variables = new VariableSet();
            variables.Set("Squid.Deployment.Id", "42");

            var renderer = new TokenSubstitutingYamlManifestRenderer();
            var rendered = await renderer.RenderAsync(
                new KubernetesApplyRequest
                {
                    WorkingDirectory = tempDir,
                    YamlFilePath = yamlPath,
                    Variables = variables,
                    TemporaryFiles = new List<string>()
                },
                new ResolvedKubernetesManifestSource
                {
                    ManifestRootDirectory = tempDir,
                    ManifestFilePaths = [yamlPath]
                },
                CancellationToken.None);

            var output = File.ReadAllText(rendered.ApplyPath);
            output.ShouldNotContain("squid.io/deploy-id");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RenderAsync_MultiFile_InjectsAnnotationOnlyIntoWorkloads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-renderer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var sourceDir = Path.Combine(tempDir, "source");
            Directory.CreateDirectory(sourceDir);

            var deployFile = Path.Combine(sourceDir, "deployment.yaml");
            var configFile = Path.Combine(sourceDir, "configmap.yaml");
            File.WriteAllText(deployFile, "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: my-app\nspec:\n  template:\n    spec:\n      containers:\n      - name: app\n        image: nginx\n");
            File.WriteAllText(configFile, "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: my-config\ndata:\n  key: value\n");

            var variables = new VariableSet();
            variables.Set("Squid.Deployment.Id", "99");

            var renderer = new TokenSubstitutingYamlManifestRenderer();
            var rendered = await renderer.RenderAsync(
                new KubernetesApplyRequest
                {
                    WorkingDirectory = tempDir,
                    YamlFilePath = sourceDir,
                    Variables = variables,
                    TemporaryFiles = new List<string>()
                },
                new ResolvedKubernetesManifestSource
                {
                    ManifestRootDirectory = sourceDir,
                    ManifestFilePaths = [deployFile, configFile]
                },
                CancellationToken.None);

            var deployOutput = File.ReadAllText(Path.Combine(rendered.ApplyPath, "deployment.yaml"));
            var configOutput = File.ReadAllText(Path.Combine(rendered.ApplyPath, "configmap.yaml"));

            deployOutput.ShouldContain("squid.io/deploy-id");
            configOutput.ShouldNotContain("squid.io/deploy-id");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RenderAsync_SingleFile_TokenSubstitutionAndDeployId_BothApplied()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-renderer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var yamlContent = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: #{AppName}\nspec:\n  replicas: #{Replicas}\n  template:\n    metadata:\n      labels:\n        app: #{AppName}\n    spec:\n      containers:\n      - name: app\n        image: #{Image}\n";
            var yamlPath = Path.Combine(tempDir, "deploy.yaml");
            File.WriteAllText(yamlPath, yamlContent);

            var variables = new VariableSet();
            variables.Set("AppName", "my-app");
            variables.Set("Replicas", "3");
            variables.Set("Image", "nginx:1.25");
            variables.Set("Squid.Deployment.Id", "777");

            var renderer = new TokenSubstitutingYamlManifestRenderer();
            var rendered = await renderer.RenderAsync(
                new KubernetesApplyRequest
                {
                    WorkingDirectory = tempDir,
                    YamlFilePath = yamlPath,
                    Variables = variables,
                    TemporaryFiles = new List<string>()
                },
                new ResolvedKubernetesManifestSource
                {
                    ManifestRootDirectory = tempDir,
                    ManifestFilePaths = [yamlPath]
                },
                CancellationToken.None);

            var output = File.ReadAllText(rendered.ApplyPath);
            output.ShouldContain("name: my-app");
            output.ShouldContain("replicas: 3");
            output.ShouldContain("image: nginx:1.25");
            output.ShouldContain("squid.io/deploy-id");
            output.ShouldContain("777");
            output.ShouldNotContain("#{");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
