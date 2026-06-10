using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Xunit;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// Pins the machine-scoped isolation mutex name — the single source of truth
/// shared by deployment dispatch (<c>HalibutMachineExecutionStrategy</c>) and
/// upgrade dispatch (<c>Linux/WindowsTentacleUpgradeStrategy</c>). Every
/// FullIsolation script dispatched to a machine carries this exact value as
/// <see cref="StartScriptCommand.IsolationMutexName"/>, so they all serialise
/// behind one writer lock on that machine's Tentacle.
/// </summary>
public sealed class ScriptIsolationMutexNamesTests
{
    [Fact]
    public void ForMachine_SameMachine_ProducesSameName()
    {
        // Same machine → identical name → every FullIsolation script on it
        // (deployment or upgrade) serialises on one lock.
        ScriptIsolationMutexNames.ForMachine(42)
            .ShouldBe(ScriptIsolationMutexNames.ForMachine(42));
    }

    [Fact]
    public void ForMachine_DifferentMachines_ProduceDifferentNames()
    {
        // Different machines → different names → separate machines don't
        // serialise against each other (they're separate agents anyway).
        ScriptIsolationMutexNames.ForMachine(42)
            .ShouldNotBe(ScriptIsolationMutexNames.ForMachine(43));
    }

    [Theory]
    [InlineData(0, "squid/machine/0")]
    [InlineData(7, "squid/machine/7")]
    [InlineData(1234567890, "squid/machine/1234567890")]
    public void ForMachine_FormatPinned(int machineId, string expected)
    {
        // Deployment + upgrade dispatch BOTH call this; deploy<->upgrade
        // serialisation only holds if they produce the byte-identical string.
        // Pin the literal so a "harmless" format tweak can't silently split the
        // two onto different mutexes.
        ScriptIsolationMutexNames.ForMachine(machineId).ShouldBe(expected);
    }
}
