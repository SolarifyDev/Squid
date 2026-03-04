using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Environment;

namespace Squid.Tentacle.Tests.Kubernetes.Integration.Support;

[Trait("Category", TentacleTestCategories.Core)]
public class KubernetesE2EEnvironmentSettingsTests
{
    [Fact]
    public void Default_Settings_Are_Not_InstallReady()
    {
        var settings = new KubernetesE2EEnvironmentSettings();

        settings.Enabled.ShouldBeFalse();
        settings.HasRequiredInstallSettings.ShouldBeFalse();
        settings.DescribeMissingInstallSettings().ShouldContain("SQUID_TENTACLE_K8S_E2E_ENABLED=1");
    }

    [Fact]
    public void CanRunInstallSmoke_Requires_Enablement_Toolchain_And_InstallSettings()
    {
        var settings = new KubernetesE2EEnvironmentSettings
        {
            Enabled = true,
            RunFaultScenarios = true,
            TentacleImageRepository = "repo",
            TentacleImageTag = "tag",
            ScriptPodImage = "script",
            ServerUrl = "https://example.com",
            BearerToken = "token"
        };

        settings.CanRunInstallSmoke(new KubernetesIntegrationPrerequisites(true, true, true)).ShouldBeTrue();
        settings.CanRunInstallSmoke(new KubernetesIntegrationPrerequisites(true, false, true)).ShouldBeFalse();
        settings.CanRunFaultScenarioSmoke(new KubernetesIntegrationPrerequisites(true, true, true)).ShouldBeTrue();
    }

    [Fact]
    public void Default_Tentacle_Selectors_Are_Derived_From_ReleaseName()
    {
        var settings = new KubernetesE2EEnvironmentSettings
        {
            ReleaseName = "kubernetes-agent"
        };

        settings.GetTentacleDeploymentName().ShouldBe("kubernetes-agent");
        settings.GetTentaclePodLabelSelector().ShouldBe("app.kubernetes.io/instance=kubernetes-agent,app.kubernetes.io/name=kubernetes-agent");
    }
}
