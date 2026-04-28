using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// P1-Phase11.1 (audit ARCH.9 Plan A) — pin the per-ticket soft-cancellation
/// registry contract.
///
/// <para><b>Why this exists</b>: the agent's <see cref="IScriptService"/>
/// wire contract is SYNC (Halibut V1 design) so RPC handlers can't
/// receive a real CancellationToken from the wire. Pre-Phase-11.1, this
/// meant the agent's in-flight async work (DataStream file save, mutex
/// acquire) was stuck with hardcoded <c>CancellationToken.None</c> — even
/// when the server sent a <c>CancelScript</c> RPC, the agent's already-
/// running file writes would complete unaffected.</para>
///
/// <para>The registry is the agent-side soft-cancel mechanism: each
/// <c>StartScript</c> registers a per-ticket CTS, internal async work
/// uses its token, <c>CancelScript</c> flips it, in-flight file writes
/// observe the cancellation. <c>CompleteScript</c> disposes.</para>
///
/// <para>Once Plan B (V2 wire contract with first-class CT) lands, this
/// registry can be retired or kept as the bridge between wire CT and
/// internal CT.</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class ScriptCancellationRegistryTests
{
    [Fact]
    public void GetOrCreate_NewTicket_ReturnsUncancelledToken()
    {
        var registry = new ScriptCancellationRegistry();
        var ticket = new ScriptTicket("task-1");

        var token = registry.GetOrCreate(ticket);

        token.IsCancellationRequested.ShouldBeFalse(customMessage:
            "Fresh registry entry must yield an uncancelled token.");
    }

    [Fact]
    public void GetOrCreate_SameTicketTwice_ReturnsSameToken()
    {
        // Idempotent — concurrent registrations for the same ticket must
        // share state. Otherwise CancelScript would flip a different CTS
        // than StartScript registered, breaking the soft-cancel contract.
        var registry = new ScriptCancellationRegistry();
        var ticket = new ScriptTicket("task-1");

        var t1 = registry.GetOrCreate(ticket);
        var t2 = registry.GetOrCreate(ticket);

        // Tokens compare via internal CTS reference equality
        t1.ShouldBe(t2);
    }

    [Fact]
    public void Cancel_FlipsRegisteredToken()
    {
        var registry = new ScriptCancellationRegistry();
        var ticket = new ScriptTicket("task-1");

        var token = registry.GetOrCreate(ticket);
        token.IsCancellationRequested.ShouldBeFalse();

        registry.Cancel(ticket);

        token.IsCancellationRequested.ShouldBeTrue(customMessage:
            "After Cancel, the previously-issued token must observe IsCancellationRequested=true.");
    }

    [Fact]
    public void Cancel_UnknownTicket_NoOp()
    {
        // Defensive: CancelScript can race with StartScript or arrive
        // before the agent has registered the ticket. Must NOT throw.
        var registry = new ScriptCancellationRegistry();

        Should.NotThrow(() => registry.Cancel(new ScriptTicket("never-registered")));
    }

    [Fact]
    public void Cancel_BeforeGetOrCreate_LaterTokenObservesCancellation()
    {
        // Defensive: even if Cancel arrives BEFORE the StartScript-side
        // GetOrCreate (out-of-order RPC delivery), the eventual handler
        // must observe the cancellation. Otherwise a fast cancel could
        // race past the slow start.
        var registry = new ScriptCancellationRegistry();
        var ticket = new ScriptTicket("task-1");

        registry.Cancel(ticket);  // arrives first
        var token = registry.GetOrCreate(ticket);

        token.IsCancellationRequested.ShouldBeTrue(customMessage:
            "Late GetOrCreate after early Cancel must yield an already-cancelled token.");
    }

    [Fact]
    public void Cleanup_RemovesEntry_NextGetOrCreateIsFresh()
    {
        // After CompleteScript, the agent calls Cleanup. A subsequent
        // ticket reuse (which shouldn't happen but might) must yield a
        // fresh CTS rather than the previously-cancelled one.
        var registry = new ScriptCancellationRegistry();
        var ticket = new ScriptTicket("task-1");

        var t1 = registry.GetOrCreate(ticket);
        registry.Cancel(ticket);
        registry.Cleanup(ticket);

        var t2 = registry.GetOrCreate(ticket);
        t2.IsCancellationRequested.ShouldBeFalse(customMessage:
            "Post-cleanup, a re-registered ticket must yield a fresh uncancelled token.");
    }

    [Fact]
    public void Cleanup_UnknownTicket_NoOp()
    {
        var registry = new ScriptCancellationRegistry();
        Should.NotThrow(() => registry.Cleanup(new ScriptTicket("never-registered")));
    }

    [Fact]
    public void HighConcurrency_GetOrCreateAndCancel_NoTornUpdates()
    {
        // 50 parallel ops on the same ticket must produce a consistent
        // final state. Tests Interlocked / ConcurrentDictionary correctness
        // under contention.
        var registry = new ScriptCancellationRegistry();
        var ticket = new ScriptTicket("hot-ticket");

        Parallel.For(0, 50, _ =>
        {
            var t = registry.GetOrCreate(ticket);
            // half the parallel ops cancel, half observe
            if (System.Threading.Thread.CurrentThread.ManagedThreadId % 2 == 0)
                registry.Cancel(ticket);
        });

        // Some Cancel call has fired → token must be cancelled
        registry.GetOrCreate(ticket).IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Cleanup_RemovesEntryFromRegistry_PostCleanupYieldsNewInstance()
    {
        // CTS implements IDisposable; leak would accumulate one CTS per
        // ticket forever. Cleanup must remove the entry so subsequent
        // GetOrCreate yields a NEW (uncancelled) CTS. We verify
        // observability via fresh-instance issuance — the Register-throws
        // approach is brittle across .NET versions (some allow Register
        // on tokens whose CTS was disposed-while-uncancelled).
        var registry = new ScriptCancellationRegistry();
        var ticket = new ScriptTicket("task-1");

        var t1 = registry.GetOrCreate(ticket);
        registry.Cancel(ticket);  // mark t1 cancelled
        registry.Cleanup(ticket);

        var t2 = registry.GetOrCreate(ticket);

        t2.IsCancellationRequested.ShouldBeFalse(customMessage:
            "Post-cleanup the entry MUST be removed so a re-registered ticket yields a fresh, " +
            "uncancelled token. If this regresses, leaked CTS instances would cause the registry " +
            "to grow unbounded over the agent's lifetime.");
        // Internal state observation: t2 is a different CTS than t1
        t2.ShouldNotBe(t1);
    }
}
