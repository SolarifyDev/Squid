using Squid.Tentacle.Tests.Support;

namespace Squid.Tentacle.Tests.Kubernetes.Integration.Support;

[Trait("Category", TentacleTestCategories.Core)]
public class SquidTentacleHelmValuesOverrideBuilderTests
{
    [Fact]
    public void BuildYaml_Contains_Key_Overrides_For_InstallSmoke()
    {
        var yaml = SquidTentacleHelmValuesOverrideBuilder.BuildYaml(new SquidTentacleHelmValuesOverride
        {
            TentacleImageRepository = "repo/tentacle",
            TentacleImageTag = "1.2.3",
            ScriptPodImage = "repo/script:1.0",
            ServerUrl = "https://squid.example.com",
            BearerToken = "token",
            KubernetesNamespace = "apps",
            WorkspaceStorageClassName = "fast-rwx",
            ForceReadWriteOnceForSmoke = true
        });

        yaml.ShouldContain("repository: \"repo/tentacle\"");
        yaml.ShouldContain("tag: \"1.2.3\"");
        yaml.ShouldContain("serverUrl: \"https://squid.example.com\"");
        yaml.ShouldContain("bearerToken: \"token\"");
        yaml.ShouldContain("image: \"repo/script:1.0\"");
        yaml.ShouldContain("namespace: \"apps\"");
        yaml.ShouldContain("storageClassName: \"fast-rwx\"");
        yaml.ShouldContain("- ReadWriteOnce");
    }

    [Fact]
    public void BuildYaml_Escapes_Quotes_And_Backslashes()
    {
        var yaml = SquidTentacleHelmValuesOverrideBuilder.BuildYaml(new SquidTentacleHelmValuesOverride
        {
            TentacleImageRepository = "repo\\path",
            TentacleImageTag = "tag\"quoted",
            ScriptPodImage = "script",
            ServerUrl = "https://squid.example.com",
            BearerToken = "abc\"def",
            KubernetesNamespace = "default"
        });

        yaml.ShouldContain("repository: \"repo\\\\path\"");
        yaml.ShouldContain("tag: \"tag\\\"quoted\"");
        yaml.ShouldContain("bearerToken: \"abc\\\"def\"");
    }
}
