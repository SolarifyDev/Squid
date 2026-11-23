using Squid.Core.Halibut;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;

namespace Squid.UnitTests.Halibut.ScriptServiceV2;

/// <summary>
/// Fidelity tier: UNIT. Pins the wire-visible numbers/strings of the ScriptService V2 contract and the
/// capability handshake. Renumbering an enum member or renaming the capability string breaks mixed-version
/// agents/servers, so each value is asserted literally.
/// </summary>
public sealed class ScriptServiceV2ContractTests
{
    [Theory]
    [InlineData(ScriptStateV2.Unspecified, 0)]
    [InlineData(ScriptStateV2.Pending, 1)]
    [InlineData(ScriptStateV2.Running, 2)]
    [InlineData(ScriptStateV2.Completed, 3)]
    [InlineData(ScriptStateV2.UnknownTicket, 4)]
    [InlineData(ScriptStateV2.ResultLost, 5)]
    public void ScriptStateV2_ValuesPinned(ScriptStateV2 state, int wireValue)
    {
        ((int)state).ShouldBe(wireValue, "renumbering ScriptStateV2 breaks every mixed-version agent/server pair");
    }

    [Fact]
    public void ScriptStateV2_HasNoUnpinnedMembers()
    {
        Enum.GetValues<ScriptStateV2>().Length.ShouldBe(6, "a new member must be added to the pinned theory above");
    }

    [Theory]
    [InlineData(CompleteScriptOutcomeV2.Unspecified, 0)]
    [InlineData(CompleteScriptOutcomeV2.Completed, 1)]
    [InlineData(CompleteScriptOutcomeV2.AlreadyCompleted, 2)]
    [InlineData(CompleteScriptOutcomeV2.UnknownTicket, 3)]
    [InlineData(CompleteScriptOutcomeV2.NotYetComplete, 4)]
    public void CompleteScriptOutcomeV2_ValuesPinned(CompleteScriptOutcomeV2 outcome, int wireValue)
    {
        ((int)outcome).ShouldBe(wireValue);
    }

    [Fact]
    public void CompleteScriptOutcomeV2_HasNoUnpinnedMembers()
    {
        Enum.GetValues<CompleteScriptOutcomeV2>().Length.ShouldBe(5, "a new member must be added to the pinned theory above");
    }

    [Fact]
    public void StartScriptCommandV2_IsolationMutexTimeout_DefaultsToWhatTheServerSendsForV1()
    {
        var command = new StartScriptCommandV2 { ScriptTicket = new ScriptTicket("t") };

        command.ScriptIsolationMutexTimeout.ShouldBe(TimeSpan.FromMinutes(30), "zero makes the agent's mutex acquire fail even for NoIsolation");
        StartScriptCommandV2.DefaultScriptIsolationMutexTimeout.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void ScriptServiceCapabilities_V2_ConstantPinned()
    {
        ScriptServiceCapabilities.V2.ShouldBe("IScriptService/v2");
    }

    [Fact]
    public void UnknownTicket_IsRepresentedByStateNotByAnExitCode()
    {
        var unknown = new ScriptStatusResponseV2(new ScriptTicket("t"), ScriptStateV2.UnknownTicket, 0, new List<ProcessOutput>(), 0);
        var exitedMinusOne = new ScriptStatusResponseV2(new ScriptTicket("t"), ScriptStateV2.Completed, ScriptExitCodes.UnknownResult, new List<ProcessOutput>(), 0);

        unknown.HasExitCode().ShouldBeFalse();
        exitedMinusOne.HasExitCode().ShouldBeTrue("a script that genuinely exits -1 is Completed, never confused with an unknown ticket");
        exitedMinusOne.State.ShouldNotBe(unknown.State);
    }

    [Theory]
    [InlineData(ScriptStateV2.Pending, false)]
    [InlineData(ScriptStateV2.Running, false)]
    [InlineData(ScriptStateV2.Completed, true)]
    [InlineData(ScriptStateV2.UnknownTicket, false)]
    [InlineData(ScriptStateV2.ResultLost, false)]
    public void HasExitCode_OnlyWhenCompleted(ScriptStateV2 state, bool expected)
    {
        new ScriptStatusResponseV2(new ScriptTicket("t"), state, 0, new List<ProcessOutput>(), 0).HasExitCode().ShouldBe(expected);
    }

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "IScriptService/v1" }, false)]
    [InlineData(new[] { "IScriptService/v1", "ICapabilitiesService/v1" }, false)]
    [InlineData(new[] { "IScriptService/v1", "IScriptService/v2" }, true)]
    [InlineData(new[] { "ICapabilitiesService/v1", "iscriptservice/V2" }, true)]
    [InlineData(new[] { "IScriptService/v20" }, false)]
    public void SupportsScriptServiceV2_TrueOnlyWhenV2Advertised(string[] advertised, bool expected)
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();

        cache.Store(1, new Dictionary<string, string>(), "1.0.0", advertised);

        cache.TryGet(1).SupportsScriptServiceV2.ShouldBe(expected);
    }

    [Fact]
    public async Task SupportsScriptServiceV2_SynthesisedBackCompatCapabilities_IsFalse()
    {
        var inner = new Mock<IAsyncCapabilitiesService>();
        inner.Setup(i => i.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>())).ThrowsAsync(new global::Halibut.Exceptions.ServiceNotFoundHalibutClientException("old agent"));
        var synthesised = await new BackwardsCompatibleCapabilitiesClient(inner.Object).GetCapabilitiesAsync(new CapabilitiesRequest());
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();

        cache.Store(1, new Dictionary<string, string>(), synthesised.AgentVersion, synthesised.SupportedServices);

        cache.TryGet(1).SupportsScriptServiceV2.ShouldBeFalse("an old agent must keep dispatching through V1");
    }

    [Fact]
    public void StartScriptCommandV2_NullTicket_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new StartScriptCommandV2 { ScriptTicket = null });
    }

    [Fact]
    public void StartScriptCommandV2_Defaults_CollectionsEmptyAndSyntaxBash()
    {
        var command = new StartScriptCommandV2 { ScriptTicket = new ScriptTicket("t") };

        command.Arguments.ShouldBeEmpty();
        command.Files.ShouldBeEmpty();
        command.Scripts.ShouldBeEmpty();
        command.ScriptSyntax.ShouldBe(ScriptType.Bash);
    }
}
