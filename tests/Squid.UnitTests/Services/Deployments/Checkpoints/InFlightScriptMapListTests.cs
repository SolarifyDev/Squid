using System.Linq;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>Reading back EVERY recorded in-flight script (what cancelling a paused task needs): slot, ticket and the dialect it was started with.</summary>
public class InFlightScriptMapListTests
{
    [Fact]
    public void List_ReturnsEveryEntry_WithItsSlotTicketAndVersion()
    {
        var json = InFlightScriptMap.Add("[]", new DispatchSlot(1, 10, 100), "a", ScriptProtocolVersion.V1);
        json = InFlightScriptMap.Add(json, new DispatchSlot(2, 11, 101), "b", ScriptProtocolVersion.V2);

        var scripts = InFlightScriptMap.List(json);

        scripts.ShouldBe([new InFlightScript(new DispatchSlot(1, 10, 100), "a", ScriptProtocolVersion.V1), new InFlightScript(new DispatchSlot(2, 11, 101), "b", ScriptProtocolVersion.V2)]);
    }

    [Fact]
    public void List_ARecordWithoutAVersion_IsV1()
        => InFlightScriptMap.List("""[{"m":7,"s":11,"a":22,"t":"legacy"}]""").Single().Protocol.ShouldBe(ScriptProtocolVersion.V1);

    // Not V1: probing or cancelling a script in a dialect it was not started with is exactly the mistake the version field exists to prevent.
    [Fact]
    public void List_AnUnrecognisedVersion_IsNullNotV1()
        => InFlightScriptMap.List("""[{"m":7,"s":11,"a":22,"t":"future","v":"V9"}]""").Single().Protocol.ShouldBeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("not json")]
    public void List_NothingOrMalformed_IsEmpty(string json)
        => InFlightScriptMap.List(json).ShouldBeEmpty();
}
