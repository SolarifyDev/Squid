using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Machines.Upgrade;
using Squid.IntegrationTests.Base;

namespace Squid.IntegrationTests.Services.Machines.Upgrade;

/// <summary>
/// Real-DB coverage for the durable upgrade trace (Rule 9 integration tier):
/// exercises <see cref="UpgradeTracePersistence"/> against a live Postgres,
/// proving the snapshot writes to / loads from the
/// <c>machine.last_upgrade_trace_json</c> column, and that a simulated server
/// restart (a fresh in-memory store hydrated from the DB) restores the trace —
/// the whole point of persisting the terminal outcome.
/// </summary>
public sealed class UpgradeTracePersistenceIntegrationTests : TestBase
{
    public UpgradeTracePersistenceIntegrationTests()
        : base("UpgradeTracePersistence", "squid_it_upgrade_trace")
    {
    }

    [Fact]
    public async Task SaveAsync_WritesTraceJsonAndTimestamp_ToMachineRow()
    {
        var machineId = await SeedMachineAsync();

        await Run<IUpgradeTracePersistence>(p => p.SaveAsync(machineId, SnapshotFor("SUCCESS", exitCode: 0), CancellationToken.None));

        await Run<IRepository>(async repo =>
        {
            var machine = await repo.QueryNoTracking<Machine>(m => m.Id == machineId).FirstAsync(CancellationToken.None).ConfigureAwait(false);

            machine.LastUpgradeTraceJson.ShouldNotBeNull();
            // jsonb normalises formatting on read-back (reorders keys, adds a
            // space after ':'), so assert on the value substring rather than an
            // exact serialised layout. The round-trip shape is verified precisely
            // by LoadAllAsync_RoundTripsSavedSnapshot (which deserialises).
            machine.LastUpgradeTraceJson.ShouldContain("SUCCESS");
            machine.LastUpgradeTraceUpdatedAt.ShouldNotBeNull(
                customMessage: "persisting a terminal trace must stamp last_upgrade_trace_updated_at so admin queries can sort by recency.");
        });
    }

    [Fact]
    public async Task LoadAllAsync_RoundTripsSavedSnapshot()
    {
        var machineId = await SeedMachineAsync();

        await Run<IUpgradeTracePersistence>(p => p.SaveAsync(machineId, SnapshotFor("FAILED", exitCode: 7), CancellationToken.None));

        var loaded = await Run<IUpgradeTracePersistence, (int, UpgradeTraceSnapshot)?>(async p =>
        {
            var all = await p.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var row in all)
                if (row.MachineId == machineId) return row;
            return null;
        });

        loaded.ShouldNotBeNull("the saved machine must come back from LoadAllAsync.");
        var snapshot = loaded.Value.Item2;
        snapshot.Status.Status.ShouldBe("FAILED");
        snapshot.Status.ExitCode.ShouldBe(7);
        snapshot.Events.Count.ShouldBe(2);
        snapshot.Events[1].Kind.ShouldBe("done");
        snapshot.Log.ShouldContain("Restarting service");
    }

    [Fact]
    public async Task SaveAsync_Twice_LatestOutcomeWins()
    {
        var machineId = await SeedMachineAsync();

        await Run<IUpgradeTracePersistence>(p => p.SaveAsync(machineId, SnapshotFor("FAILED", exitCode: 7), CancellationToken.None));
        await Run<IUpgradeTracePersistence>(p => p.SaveAsync(machineId, SnapshotFor("SUCCESS", exitCode: 0), CancellationToken.None));

        var status = await LoadStatusAsync(machineId);

        status.ShouldBe("SUCCESS", "a later terminal outcome (a re-run upgrade) must overwrite the earlier one — the column holds the LATEST trace.");
    }

    [Fact]
    public async Task LoadAllAsync_SkipsMachinesWithNoTrace()
    {
        // A machine that never had a terminal upgrade has NULL columns and must
        // not appear in the hydrate set (its in-memory timeline stays empty).
        var machineId = await SeedMachineAsync();

        var present = await Run<IUpgradeTracePersistence, bool>(async p =>
        {
            var all = await p.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var row in all)
                if (row.MachineId == machineId) return true;
            return false;
        });

        present.ShouldBeFalse("a machine with NULL last_upgrade_trace_json must be skipped by LoadAllAsync.");
    }

    [Fact]
    public async Task LoadAllAsync_SkipsUndeserialisableRow_DoesNotThrow()
    {
        // A jsonb column guarantees well-formed JSON (Postgres rejects malformed
        // input at write time), so the realistic corruption is a VALID-JSON blob
        // of the wrong shape — e.g. schema drift to an incompatible structure. A
        // root-kind mismatch (array where an object is expected) makes
        // Deserialize<UpgradeTraceSnapshot> throw; LoadAllAsync must log + skip
        // it, never crash startup hydration.
        var goodId = await SeedMachineAsync();
        var badId = await SeedMachineAsync();

        await Run<IUpgradeTracePersistence>(p => p.SaveAsync(goodId, SnapshotFor("SUCCESS", exitCode: 0), CancellationToken.None));

        await Run<IRepository>(repo => repo.ExecuteUpdateAsync<Machine>(
            m => m.Id == badId,
            s => s.SetProperty(m => m.LastUpgradeTraceJson, "[1, 2, 3]"),
            CancellationToken.None));

        var loaded = await Run<IUpgradeTracePersistence, System.Collections.Generic.List<int>>(async p =>
        {
            var all = await p.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);
            var ids = new System.Collections.Generic.List<int>();
            foreach (var row in all) ids.Add(row.MachineId);
            return ids;
        });

        loaded.ShouldContain(goodId, "the well-formed row must still load despite a sibling corrupt row.");
        loaded.ShouldNotContain(badId, "the malformed row must be skipped, not surfaced or thrown.");
    }

    [Fact]
    public async Task Durability_SimulatedRestart_HydratorRestoresTraceIntoFreshStore()
    {
        // End-to-end proof of #2: persist a terminal outcome, then simulate a
        // server pod restart with a brand-new (empty) in-memory store + gate,
        // hydrate it from the DB, and confirm the operator-visible timeline is
        // restored — status, events, AND Phase B log.
        var machineId = await SeedMachineAsync();

        await Run<IUpgradeTracePersistence>(p => p.SaveAsync(machineId, SnapshotFor("SUCCESS", exitCode: 0), CancellationToken.None));

        var freshStore = new InMemoryUpgradeEventTimelineStore();   // "server just restarted"
        var freshGate = new UpgradeTracePersistenceGate();

        await Run<IUpgradeTracePersistence>(async p =>
        {
            var all = await p.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);
            UpgradeTraceHydrator.ApplyTo(freshStore, freshGate, all);
        });

        freshStore.GetStatus(machineId).ShouldNotBeNull("post-restart hydration must restore the status payload.");
        freshStore.GetStatus(machineId).Status.ShouldBe("SUCCESS");
        freshStore.GetStatus(machineId).ExitCode.ShouldBe(0);
        freshStore.Get(machineId).Count.ShouldBe(2, "the event timeline must survive the restart.");
        freshStore.GetLog(machineId).ShouldContain("Restarting service", customMessage: "the Phase B log must survive the restart.");

        freshGate.AlreadyPersisted(machineId, SnapshotFor("SUCCESS", exitCode: 0).Signature)
            .ShouldBeTrue("hydration must prime the gate so the first post-restart probe doesn't re-write the on-disk snapshot.");
    }

    private async Task<string> LoadStatusAsync(int machineId)
    {
        return await Run<IUpgradeTracePersistence, string>(async p =>
        {
            var all = await p.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var row in all)
                if (row.MachineId == machineId) return row.Snapshot.Status.Status;
            return null;
        });
    }

    private static UpgradeTraceSnapshot SnapshotFor(string status, int exitCode) => new()
    {
        Status = new UpgradeStatusPayload
        {
            SchemaVersion = 2,
            Status = status,
            TargetVersion = "1.8.7",
            InstallMethod = "tarball",
            ExitCode = exitCode,
            UpdatedAt = DateTimeOffset.Parse("2026-06-01T10:01:30Z")
        },
        Events = new[]
        {
            new UpgradeEvent { Phase = "A", Kind = "start", Message = "Selecting upgrade method" },
            new UpgradeEvent { Phase = "B", Kind = "done", Message = "finished" }
        },
        Log = "=== In scope ===\nRestarting service...\nUpgrade successful"
    };

    private async Task<int> SeedMachineAsync()
    {
        var machineId = 0;
        var unique = Guid.NewGuid().ToString("N");

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var machine = new Machine
            {
                Name = $"trace-agent-{unique}",
                IsDisabled = false,
                Roles = System.Text.Json.JsonSerializer.Serialize(new[] { "web-server" }),
                EnvironmentIds = System.Text.Json.JsonSerializer.Serialize(new[] { 1 }),
                SpaceId = 1,
                Endpoint = """{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub","Thumbprint":"AABB"}""",
                Slug = $"trace-agent-{unique}"
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
            machineId = machine.Id;
        });

        return machineId;
    }
}
