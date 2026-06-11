using Microsoft.Extensions.Configuration;
using Shouldly;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Flavors.Tentacle;
using Squid.Tentacle.SelfHeal;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Flavors.Tentacle;

/// <summary>
/// Pins the wiring that took the disk self-heal from dead code to live: the
/// regular Tentacle flavor must schedule a <see cref="SelfHealBackgroundTask"/> as
/// a host background task. Before this, <c>TentacleFlavor</c> returned
/// <c>BackgroundTasks = []</c>, so a disk-full agent failed deployments with no
/// auto-reclaim even though the whole heal mechanism existed.
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class TentacleFlavorSelfHealWiringTests
{
    [Fact]
    public void CreateRuntime_SchedulesSelfHealBackgroundTask()
    {
        var context = new TentacleFlavorContext
        {
            TentacleSettings = new TentacleSettings(),
            Configuration = new ConfigurationBuilder().Build()
        };

        var runtime = new TentacleFlavor().CreateRuntime(context);

        var selfHeal = runtime.BackgroundTasks.OfType<SelfHealBackgroundTask>().SingleOrDefault();

        selfHeal.ShouldNotBeNull(
            customMessage: "TentacleFlavor must schedule the disk self-heal background task — otherwise the heal mechanism stays dead code and a disk-full agent fails deployments with no auto-reclaim.");
        selfHeal.Name.ShouldBe("SelfHeal");
    }
}
