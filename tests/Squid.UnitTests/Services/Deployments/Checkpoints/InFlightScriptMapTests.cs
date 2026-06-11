using Squid.Core.Services.Deployments.Checkpoints;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// Unit tests for <see cref="InFlightScriptMap"/> — the pure add/remove/lookup
/// over the checkpoint's in-flight-scripts JSON, keyed by <see cref="DispatchSlot"/>
/// (machine + step + action). Pins round-trip correctness, the dispatch-scoped
/// independence that keeps two parallel steps on ONE machine from colliding, and
/// the defensive empty/malformed handling the resume path relies on.
/// </summary>
public class InFlightScriptMapTests
{
    private static DispatchSlot Slot(int machineId, string step = "Step1", string action = "Action1")
        => new(machineId, step, action);

    [Fact]
    public void Add_OnEmpty_CreatesSingleEntry()
    {
        var json = InFlightScriptMap.Add("[]", Slot(7), "ticket-a");

        InFlightScriptMap.TryGet(json, Slot(7)).ShouldBe("ticket-a");
    }

    [Fact]
    public void Add_MultipleMachines_KeepsBoth()
    {
        var json = InFlightScriptMap.Add("[]", Slot(7), "ticket-a");
        json = InFlightScriptMap.Add(json, Slot(9), "ticket-b");

        InFlightScriptMap.TryGet(json, Slot(7)).ShouldBe("ticket-a");
        InFlightScriptMap.TryGet(json, Slot(9)).ShouldBe("ticket-b");
    }

    [Fact]
    public void Add_SameSlotTwice_OverwritesWithLatestTicket()
    {
        // A fresh dispatch (new Guid ticket) for the same dispatch slot replaces the
        // prior in-flight ticket — the latest dispatch is the one to re-attach to.
        var json = InFlightScriptMap.Add("[]", Slot(7), "ticket-old");
        json = InFlightScriptMap.Add(json, Slot(7), "ticket-new");

        InFlightScriptMap.TryGet(json, Slot(7)).ShouldBe("ticket-new");
    }

    [Fact]
    public void Add_SameMachineDifferentSteps_AreIndependent()
    {
        // The headline guarantee: two StartWithPrevious steps dispatched in parallel
        // to the SAME machine each get their OWN in-flight slot. The machine-only key
        // this replaced would have made the second dispatch overwrite (and re-attach
        // to) the first's ticket — silently skipping the second step's script.
        var json = InFlightScriptMap.Add("[]", Slot(7, "StepA", "ActionA"), "ticket-a");
        json = InFlightScriptMap.Add(json, Slot(7, "StepB", "ActionB"), "ticket-b");

        InFlightScriptMap.TryGet(json, Slot(7, "StepA", "ActionA")).ShouldBe("ticket-a");
        InFlightScriptMap.TryGet(json, Slot(7, "StepB", "ActionB")).ShouldBe("ticket-b",
            customMessage: "A second dispatch to the same machine (different step/action) MUST get its own slot, not collide with the first.");
    }

    [Fact]
    public void TryGet_SameMachineDifferentAction_DoesNotMatchSiblingSlot()
    {
        // The reattach probe for StepB MUST NOT find StepA's ticket just because they
        // target the same machine — that is exactly the cross-reattach this fixes.
        var json = InFlightScriptMap.Add("[]", Slot(7, "StepA", "ActionA"), "ticket-a");

        InFlightScriptMap.TryGet(json, Slot(7, "StepB", "ActionB")).ShouldBeNull(
            customMessage: "StepB's reattach probe must not match StepA's slot on the same machine.");
    }

    [Fact]
    public void Remove_DropsOnlyThatSlot()
    {
        var json = InFlightScriptMap.Add("[]", Slot(7, "StepA", "ActionA"), "ticket-a");
        json = InFlightScriptMap.Add(json, Slot(7, "StepB", "ActionB"), "ticket-b");
        json = InFlightScriptMap.Add(json, Slot(9), "ticket-c");

        json = InFlightScriptMap.Remove(json, Slot(7, "StepA", "ActionA"));

        InFlightScriptMap.TryGet(json, Slot(7, "StepA", "ActionA")).ShouldBeNull();
        InFlightScriptMap.TryGet(json, Slot(7, "StepB", "ActionB")).ShouldBe("ticket-b",
            customMessage: "Clearing one slot MUST NOT drop a sibling slot on the same machine.");
        InFlightScriptMap.TryGet(json, Slot(9)).ShouldBe("ticket-c");
    }

    [Fact]
    public void Remove_AbsentSlot_IsNoOp()
    {
        var json = InFlightScriptMap.Add("[]", Slot(7), "ticket-a");

        var after = InFlightScriptMap.Remove(json, Slot(999));

        InFlightScriptMap.TryGet(after, Slot(7)).ShouldBe("ticket-a");
    }

    [Fact]
    public void TryGet_AbsentSlot_ReturnsNull()
        => InFlightScriptMap.TryGet("[]", Slot(7)).ShouldBeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[ broken")]
    [InlineData("{\"11\":\"legacy-machine-keyed-ticket\"}")]
    public void EmptyMalformedOrLegacyJson_TreatedAsEmpty(string json)
    {
        // The resume path must never crash on a blank/corrupt column — nor on a row
        // written in the OLD machine-keyed object shape by an older server. All fall
        // back to "no in-flight script", i.e. re-dispatch fresh.
        InFlightScriptMap.TryGet(json, Slot(7)).ShouldBeNull();
        InFlightScriptMap.Add(json, Slot(7), "ticket-a").ShouldContain("ticket-a");
    }
}
