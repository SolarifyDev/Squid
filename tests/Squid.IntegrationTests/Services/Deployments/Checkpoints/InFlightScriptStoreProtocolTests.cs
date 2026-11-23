using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.IntegrationTests.Base;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.IntegrationTests.Services.Deployments.Checkpoints;

/// <summary>
/// The protocol version recorded with an in-flight ticket, against a real Postgres checkpoint row: it is
/// written in the SAME update as the ticket (so a record never exists with a ticket but no version),
/// read back for a resume, defaults to V1 for a row written before versions existed, and survives a
/// neighbouring slot's clear.
/// </summary>
public class InFlightScriptStoreProtocolTests : TestBase
{
    public InFlightScriptStoreProtocolTests() : base("InFlightScriptStoreProtocol", "squid_it_inflight_protocol")
    {
    }

    private static DispatchSlot Slot(int machineId, int stepId = 1, int actionId = 1) => new(machineId, stepId, actionId);

    [Theory]
    [InlineData(ScriptProtocolVersion.V1)]
    [InlineData(ScriptProtocolVersion.V2)]
    public async Task RecordWithVersion_PersistsTicketAndVersionTogether_AndReadsThemBack(ScriptProtocolVersion version)
    {
        var taskId = 710000 + (int)version;
        await EnsureRowAsync(taskId).ConfigureAwait(false);

        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, Slot(11), "ticket-a", version)).ConfigureAwait(false);

        (await GetTicketAsync(taskId, Slot(11)).ConfigureAwait(false)).ShouldBe("ticket-a");
        (await GetProtocolAsync(taskId, Slot(11)).ConfigureAwait(false)).ShouldBe(version);
    }

    [Fact]
    public async Task RowWrittenBeforeVersionsExisted_ReadsAsV1()
    {
        const int taskId = 710010;
        await EnsureRowAsync(taskId).ConfigureAwait(false);
        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, Slot(11), "legacy-ticket")).ConfigureAwait(false);

        (await GetProtocolAsync(taskId, Slot(11)).ConfigureAwait(false)).ShouldBe(ScriptProtocolVersion.V1);
        (await LoadAsync(taskId).ConfigureAwait(false)).InFlightScriptsJson.ShouldNotContain("\"v\"");
    }

    [Fact]
    public async Task NoCheckpointRow_ProtocolIsNull_SoACallerTreatsItAsV1()
        => (await GetProtocolAsync(710099, Slot(11)).ConfigureAwait(false)).ShouldBeNull();

    [Fact]
    public async Task EachSlotKeepsItsOwnVersion_AndClearingOneDoesNotTouchTheOther()
    {
        const int taskId = 710020;
        await EnsureRowAsync(taskId).ConfigureAwait(false);

        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, Slot(11, 1, 1), "ticket-a", ScriptProtocolVersion.V2)).ConfigureAwait(false);
        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, Slot(11, 2, 2), "ticket-b", ScriptProtocolVersion.V1)).ConfigureAwait(false);
        await Run<IInFlightScriptStore>(s => s.ClearAsync(taskId, Slot(11, 2, 2))).ConfigureAwait(false);

        (await GetProtocolAsync(taskId, Slot(11, 1, 1)).ConfigureAwait(false)).ShouldBe(ScriptProtocolVersion.V2);
        (await GetTicketAsync(taskId, Slot(11, 2, 2)).ConfigureAwait(false)).ShouldBeNull();
    }

    [Fact]
    public async Task ReDispatchOfTheSameSlot_ReplacesTheVersion()
    {
        const int taskId = 710030;
        await EnsureRowAsync(taskId).ConfigureAwait(false);

        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, Slot(11), "ticket-a", ScriptProtocolVersion.V2)).ConfigureAwait(false);
        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, Slot(11), "ticket-b", ScriptProtocolVersion.V1)).ConfigureAwait(false);

        (await GetTicketAsync(taskId, Slot(11)).ConfigureAwait(false)).ShouldBe("ticket-b");
        (await GetProtocolAsync(taskId, Slot(11)).ConfigureAwait(false)).ShouldBe(ScriptProtocolVersion.V1);
    }

    private Task EnsureRowAsync(int taskId) => Run<IDeploymentCheckpointService>(svc => svc.EnsureExistsAsync(taskId, deploymentId: 1));

    private Task<string> GetTicketAsync(int taskId, DispatchSlot slot) => Run<IInFlightScriptStore, string>(s => s.TryGetTicketAsync(taskId, slot));

    private Task<ScriptProtocolVersion?> GetProtocolAsync(int taskId, DispatchSlot slot) => Run<IInFlightScriptStore, ScriptProtocolVersion?>(s => s.TryGetProtocolAsync(taskId, slot));

    private Task<DeploymentExecutionCheckpoint> LoadAsync(int taskId) => Run<IDeploymentCheckpointService, DeploymentExecutionCheckpoint>(svc => svc.LoadAsync(taskId));
}
