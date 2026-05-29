using Squid.Core.Services.Deployments.Checkpoints;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// Unit tests for <see cref="InFlightScriptMap"/> — the pure add/remove/lookup
/// over the checkpoint's in-flight-scripts JSON. Pins round-trip correctness +
/// the defensive empty/malformed handling the resume path relies on.
/// </summary>
public class InFlightScriptMapTests
{
    [Fact]
    public void Add_OnEmpty_CreatesSingleEntry()
    {
        var json = InFlightScriptMap.Add("{}", machineId: 7, scriptTicket: "ticket-a");

        InFlightScriptMap.TryGet(json, 7).ShouldBe("ticket-a");
    }

    [Fact]
    public void Add_MultipleMachines_KeepsBoth()
    {
        var json = InFlightScriptMap.Add("{}", 7, "ticket-a");
        json = InFlightScriptMap.Add(json, 9, "ticket-b");

        InFlightScriptMap.TryGet(json, 7).ShouldBe("ticket-a");
        InFlightScriptMap.TryGet(json, 9).ShouldBe("ticket-b");
    }

    [Fact]
    public void Add_SameMachineTwice_OverwritesWithLatestTicket()
    {
        // A fresh dispatch (new Guid ticket) for the same machine replaces the
        // prior in-flight ticket — the latest dispatch is the one to re-attach to.
        var json = InFlightScriptMap.Add("{}", 7, "ticket-old");
        json = InFlightScriptMap.Add(json, 7, "ticket-new");

        InFlightScriptMap.TryGet(json, 7).ShouldBe("ticket-new");
    }

    [Fact]
    public void Remove_DropsOnlyThatMachine()
    {
        var json = InFlightScriptMap.Add("{}", 7, "ticket-a");
        json = InFlightScriptMap.Add(json, 9, "ticket-b");

        json = InFlightScriptMap.Remove(json, 7);

        InFlightScriptMap.TryGet(json, 7).ShouldBeNull();
        InFlightScriptMap.TryGet(json, 9).ShouldBe("ticket-b");
    }

    [Fact]
    public void Remove_AbsentMachine_IsNoOp()
    {
        var json = InFlightScriptMap.Add("{}", 7, "ticket-a");

        var after = InFlightScriptMap.Remove(json, 999);

        InFlightScriptMap.TryGet(after, 7).ShouldBe("ticket-a");
    }

    [Fact]
    public void TryGet_AbsentMachine_ReturnsNull()
        => InFlightScriptMap.TryGet("{}", 7).ShouldBeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ broken")]
    public void EmptyOrMalformedJson_TreatedAsEmpty(string json)
    {
        // The resume path must never crash on a blank/corrupt column — it falls
        // back to "no in-flight script", i.e. re-dispatch fresh.
        InFlightScriptMap.TryGet(json, 7).ShouldBeNull();
        InFlightScriptMap.Add(json, 7, "ticket-a").ShouldContain("ticket-a");
    }
}
