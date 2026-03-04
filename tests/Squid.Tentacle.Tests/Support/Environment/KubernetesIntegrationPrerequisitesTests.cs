using Squid.Tentacle.Tests.Support;

namespace Squid.Tentacle.Tests.Support.Environment;

[Trait("Category", TentacleTestCategories.Core)]
public class KubernetesIntegrationPrerequisitesTests
{
    [Fact]
    public void Detect_Returns_Consistent_Availability_And_MissingDescription()
    {
        var prereqs = KubernetesIntegrationPrerequisites.Detect();
        var missing = prereqs.DescribeMissing();

        prereqs.IsAvailable.ShouldBe(prereqs.HasHelm && prereqs.HasKubectl && prereqs.HasKind);

        if (prereqs.IsAvailable)
        {
            missing.ShouldBe(string.Empty);
        }
        else
        {
            missing.ShouldNotBeNullOrWhiteSpace();
        }
    }
}
