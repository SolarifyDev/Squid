using Squid.Core.Services.Deployments.Checkpoints;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// Unit tests for <see cref="InFlightScriptMap"/> — the pure add/remove/lookup
/// over the checkpoint's in-flight-scripts JSON, keyed by <see cref="DispatchSlot"/>
/// (machine + stable step-id + stable action-id). Pins round-trip correctness, the
/// dispatch-scoped independence that keeps two parallel steps on ONE machine from
/// colliding, and the defensive empty/malformed/legacy handling the resume path
/// relies on.
/// </summary>
public class InFlightScriptMapTests
{
    private static DispatchSlot Slot(int machineId, int stepId = 1, int actionId = 1)
        => new(machineId, stepId, actionId);

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
    public void Add_SameMachineDifferentActions_AreIndependent()
    {
        // The headline guarantee: two StartWithPrevious steps dispatched in parallel
        // to the SAME machine each get their OWN in-flight slot, keyed by the stable
        // action id. The machine-only key this replaced would have made the second
        // dispatch overwrite (and re-attach to) the first's ticket — silently
        // skipping the second step's script.
        var json = InFlightScriptMap.Add("[]", Slot(7, stepId: 10, actionId: 100), "ticket-a");
        json = InFlightScriptMap.Add(json, Slot(7, stepId: 20, actionId: 200), "ticket-b");

        InFlightScriptMap.TryGet(json, Slot(7, stepId: 10, actionId: 100)).ShouldBe("ticket-a");
        InFlightScriptMap.TryGet(json, Slot(7, stepId: 20, actionId: 200)).ShouldBe("ticket-b",
            customMessage: "A second dispatch to the same machine (different action id) MUST get its own slot, not collide with the first.");
    }

    [Fact]
    public void TryGet_SameMachineDifferentActionId_DoesNotMatchSiblingSlot()
    {
        // The reattach probe for action 200 MUST NOT find action 100's ticket just
        // because they target the same machine — that is exactly the cross-reattach
        // this fixes. Display names are irrelevant; only the id matters.
        var json = InFlightScriptMap.Add("[]", Slot(7, stepId: 10, actionId: 100), "ticket-a");

        InFlightScriptMap.TryGet(json, Slot(7, stepId: 20, actionId: 200)).ShouldBeNull(
            customMessage: "Action 200's reattach probe must not match action 100's slot on the same machine.");
    }

    [Fact]
    public void Remove_DropsOnlyThatSlot()
    {
        var json = InFlightScriptMap.Add("[]", Slot(7, stepId: 10, actionId: 100), "ticket-a");
        json = InFlightScriptMap.Add(json, Slot(7, stepId: 20, actionId: 200), "ticket-b");
        json = InFlightScriptMap.Add(json, Slot(9), "ticket-c");

        json = InFlightScriptMap.Remove(json, Slot(7, stepId: 10, actionId: 100));

        InFlightScriptMap.TryGet(json, Slot(7, stepId: 10, actionId: 100)).ShouldBeNull();
        InFlightScriptMap.TryGet(json, Slot(7, stepId: 20, actionId: 200)).ShouldBe("ticket-b",
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
    [InlineData("[{\"m\":7,\"s\":\"StepA\",\"a\":\"ActionA\",\"t\":\"legacy-name-keyed\"}]")]
    public void EmptyMalformedOrLegacyJson_TreatedAsEmpty(string json)
    {
        // The resume path must never crash on a blank/corrupt column — nor on a row
        // written in an older shape (the machine-keyed object, or the interim
        // name-keyed list whose s/a are strings not ints). All fall back to "no
        // in-flight script", i.e. re-dispatch fresh.
        InFlightScriptMap.TryGet(json, Slot(7)).ShouldBeNull();
        InFlightScriptMap.Add(json, Slot(7), "ticket-a").ShouldContain("ticket-a");
    }
}
