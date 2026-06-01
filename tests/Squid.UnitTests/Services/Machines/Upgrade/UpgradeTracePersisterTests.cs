using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Pins the durable-trace orchestration in isolation: given the in-memory store
/// state, the persister decides whether to write the snapshot (terminal + not
/// already persisted) and marks the dedup gate only on a successful write.
/// Uses the real store + real gate so the terminal/dedup contract is exercised
/// end-to-end; the DB write itself is mocked.
/// </summary>
public sealed class UpgradeTracePersisterTests
{
    private readonly InMemoryUpgradeEventTimelineStore _store = new();
    private readonly UpgradeTracePersistenceGate _gate = new();
    private readonly Mock<IUpgradeTracePersistence> _persistence = new();
    private readonly List<UpgradeTraceSnapshot> _saved = new();

    private UpgradeTracePersister Build()
    {
        _persistence.Setup(p => p.SaveAsync(It.IsAny<int>(), It.IsAny<UpgradeTraceSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<int, UpgradeTraceSnapshot, CancellationToken>((_, s, _) => _saved.Add(s))
            .Returns(Task.CompletedTask);

        return new UpgradeTracePersister(_store, _persistence.Object, _gate);
    }

    private void SeedStore(int machineId, string status, string updatedAt = "2026-06-01T10:01:00Z")
    {
        _store.StoreStatus(machineId, new UpgradeStatusPayload { Status = status, ExitCode = 0, UpdatedAt = DateTimeOffset.Parse(updatedAt) });
        _store.Store(machineId, new[] { new UpgradeEvent { Kind = "start" }, new UpgradeEvent { Kind = "done" } });
        _store.StoreLog(machineId, "phase B log");
    }

    [Fact]
    public async Task PersistIfTerminal_TerminalAndNew_SavesSnapshotAndMarksGate()
    {
        var persister = Build();
        SeedStore(1, "SUCCESS");

        await persister.PersistIfTerminalAsync(1, CancellationToken.None);

        _saved.Count.ShouldBe(1);
        _saved[0].Status.Status.ShouldBe("SUCCESS");
        _saved[0].Events.Count.ShouldBe(2);
        _saved[0].Log.ShouldBe("phase B log");
        _gate.AlreadyPersisted(1, _saved[0].Signature).ShouldBeTrue();
    }

    [Fact]
    public async Task PersistIfTerminal_NoStatusCached_DoesNothing()
    {
        var persister = Build();

        await persister.PersistIfTerminalAsync(99, CancellationToken.None);

        _persistence.Verify(p => p.SaveAsync(It.IsAny<int>(), It.IsAny<UpgradeTraceSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("IN_PROGRESS")]
    [InlineData("SWAPPED")]
    [InlineData("ROLLING_BACK")]
    public async Task PersistIfTerminal_InFlight_DoesNotSave(string inFlight)
    {
        var persister = Build();
        SeedStore(2, inFlight);

        await persister.PersistIfTerminalAsync(2, CancellationToken.None);

        _persistence.Verify(p => p.SaveAsync(It.IsAny<int>(), It.IsAny<UpgradeTraceSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PersistIfTerminal_AlreadyPersistedSameOutcome_DoesNotSaveAgain()
    {
        var persister = Build();
        SeedStore(3, "SUCCESS");

        await persister.PersistIfTerminalAsync(3, CancellationToken.None);
        await persister.PersistIfTerminalAsync(3, CancellationToken.None);
        await persister.PersistIfTerminalAsync(3, CancellationToken.None);

        _persistence.Verify(p => p.SaveAsync(3, It.IsAny<UpgradeTraceSnapshot>(), It.IsAny<CancellationToken>()), Times.Once,
            "the same terminal outcome the agent keeps reporting must be persisted exactly once.");
    }

    [Fact]
    public async Task PersistIfTerminal_NewOutcomeAfterPriorPersist_SavesAgain()
    {
        var persister = Build();

        SeedStore(4, "FAILED", updatedAt: "2026-06-01T10:00:00Z");
        await persister.PersistIfTerminalAsync(4, CancellationToken.None);

        // A re-run upgrade concludes later with a different outcome → new signature.
        SeedStore(4, "SUCCESS", updatedAt: "2026-06-02T09:00:00Z");
        await persister.PersistIfTerminalAsync(4, CancellationToken.None);

        _persistence.Verify(p => p.SaveAsync(4, It.IsAny<UpgradeTraceSnapshot>(), It.IsAny<CancellationToken>()), Times.Exactly(2),
            "a genuinely new terminal outcome (different signature) must be persisted even after a prior one.");
    }

    [Fact]
    public async Task PersistIfTerminal_SaveThrows_DoesNotThrow_GateLeftOpenForRetry()
    {
        _persistence.Setup(p => p.SaveAsync(It.IsAny<int>(), It.IsAny<UpgradeTraceSnapshot>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var persister = new UpgradeTracePersister(_store, _persistence.Object, _gate);
        SeedStore(5, "SUCCESS");

        await Should.NotThrowAsync(() => persister.PersistIfTerminalAsync(5, CancellationToken.None));

        var signature = new UpgradeTraceSnapshot { Status = _store.GetStatus(5) }.Signature;
        _gate.AlreadyPersisted(5, signature).ShouldBeFalse(
            customMessage: "a failed write must NOT mark the gate, so the next probe retries the persist.");
    }
}
