using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// The protocol version recorded with an in-flight script. A resume must probe in the dialect the script
/// was dispatched with, so the version has to survive the JSON round trip; and because the format is
/// additive, a record written before versions existed (no <c>"v"</c>) must read as V1 and a V1 record
/// must stay byte-identical to what a server without version negotiation wrote and reads.
/// </summary>
public class InFlightScriptMapProtocolTests
{
    private static readonly DispatchSlot Slot = new(7, 11, 22);

    [Theory]
    [InlineData(ScriptProtocolVersion.V1)]
    [InlineData(ScriptProtocolVersion.V2)]
    public void Add_WithProtocol_RoundTripsTheVersion_AndTheTicket(ScriptProtocolVersion version)
    {
        var json = InFlightScriptMap.Add("[]", Slot, "ticket-a", version);

        InFlightScriptMap.GetProtocol(json, Slot).ShouldBe(version);
        InFlightScriptMap.TryGet(json, Slot).ShouldBe("ticket-a");
    }

    [Fact]
    public void V1Record_OmitsTheVersionField_SoItStaysByteIdenticalToTheLegacyShape()
    {
        var withVersion = InFlightScriptMap.Add("[]", Slot, "ticket-a", ScriptProtocolVersion.V1);
        var legacy = InFlightScriptMap.Add("[]", Slot, "ticket-a");

        withVersion.ShouldBe(legacy);
        withVersion.ShouldBe("""[{"m":7,"s":11,"a":22,"t":"ticket-a"}]""");
    }

    [Fact]
    public void V2Record_CarriesTheVersionAsAnAdditiveField()
        => InFlightScriptMap.Add("[]", Slot, "ticket-a", ScriptProtocolVersion.V2).ShouldBe("""[{"m":7,"s":11,"a":22,"t":"ticket-a","v":"V2"}]""");

    [Fact]
    public void LegacyRecordWithoutTheField_IsV1()
        => InFlightScriptMap.GetProtocol("""[{"m":7,"s":11,"a":22,"t":"ticket-a"}]""", Slot).ShouldBe(ScriptProtocolVersion.V1);

    [Fact]
    public void RecordWrittenByANewerServer_WithAnUnknownVersion_ThrowsInsteadOfGuessingV1()
    {
        var json = """[{"m":7,"s":11,"a":22,"t":"ticket-a","v":"V9"}]""";

        var ex = Should.Throw<UnsupportedScriptProtocolException>(() => InFlightScriptMap.GetProtocol(json, Slot));

        ex.Protocol.ShouldBe("V9");
        InFlightScriptMap.TryGet(json, Slot).ShouldBe("ticket-a", "the ticket is still readable; only the dialect is unknown");
    }

    [Fact]
    public void ReDispatchOfTheSameSlot_ReplacesTheVersionAlongWithTheTicket()
    {
        var json = InFlightScriptMap.Add("[]", Slot, "ticket-a", ScriptProtocolVersion.V2);
        json = InFlightScriptMap.Add(json, Slot, "ticket-b", ScriptProtocolVersion.V1);

        InFlightScriptMap.TryGet(json, Slot).ShouldBe("ticket-b");
        InFlightScriptMap.GetProtocol(json, Slot).ShouldBe(ScriptProtocolVersion.V1);
    }

    [Fact]
    public void EachSlotKeepsItsOwnVersion()
    {
        var other = new DispatchSlot(7, 12, 23);
        var json = InFlightScriptMap.Add("[]", Slot, "ticket-a", ScriptProtocolVersion.V2);
        json = InFlightScriptMap.Add(json, other, "ticket-b", ScriptProtocolVersion.V1);

        InFlightScriptMap.GetProtocol(json, Slot).ShouldBe(ScriptProtocolVersion.V2);
        InFlightScriptMap.GetProtocol(json, other).ShouldBe(ScriptProtocolVersion.V1);
        InFlightScriptMap.ContainsMachine(json, 7).ShouldBeTrue();
    }
}
