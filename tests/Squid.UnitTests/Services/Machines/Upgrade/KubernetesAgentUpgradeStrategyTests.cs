using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Phase 1 placeholder behaviour: returns <c>NotSupported</c> with a clear
/// helm-shaped remediation hint so operators know the manual workaround
/// while Phase 2 is being implemented.
/// </summary>
public sealed class KubernetesAgentUpgradeStrategyTests
{
    private readonly KubernetesAgentUpgradeStrategy _strategy = new();

    [Fact]
    public void CanHandle_KubernetesAgentStyle_True()
    {
        _strategy.CanHandle(nameof(CommunicationStyle.KubernetesAgent)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("TentaclePolling")]
    [InlineData("TentacleListening")]
    [InlineData("KubernetesApi")]
    [InlineData("Ssh")]
    [InlineData("")]
    [InlineData(null)]
    public void CanHandle_OtherStyles_False(string style)
    {
        _strategy.CanHandle(style).ShouldBeFalse();
    }

    [Fact]
    public async Task UpgradeAsync_ReturnsNotSupportedWithHelmRemediation()
    {
        var result = await _strategy.UpgradeAsync(
            new Machine { Id = 1, Name = "k8s-agent" },
            "1.4.0",
            CancellationToken.None);

        result.Status.ShouldBe(MachineUpgradeStatus.NotSupported);
        result.Detail.ShouldContain("helm upgrade");
        result.Detail.ShouldContain("1.4.0");
    }
}
