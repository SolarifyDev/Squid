using System.Collections.Concurrent;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Tentacle.ScriptExecution;

/// <summary>
/// P1-Phase11.1 (audit ARCH.9 Plan A) — per-ticket soft-cancellation registry.
///
/// <para><b>Why this exists</b>: the agent's <see cref="IScriptService"/>
/// wire contract is SYNC (Halibut V1 design) so RPC handlers can't
/// receive a real <see cref="CancellationToken"/> from the wire. This
/// registry is the agent-side soft-cancel mechanism that bridges the
/// wire-level limitation: <see cref="LocalScriptService.StartScript"/>
/// registers a per-ticket CTS, internal async work uses its token, the
/// matching <see cref="LocalScriptService.CancelScript"/> RPC flips the
/// CTS, in-flight file writes / mutex acquires observe the cancellation.
/// <see cref="LocalScriptService.CompleteScript"/> calls
/// <see cref="Cleanup"/> to dispose the CTS.</para>
///
/// <para><b>Out-of-order RPC delivery defence</b>: if Cancel arrives
/// BEFORE GetOrCreate (server cancelled before agent has registered),
/// the early Cancel marks an "early-cancelled" placeholder. The eventual
/// GetOrCreate observes the placeholder and yields an already-cancelled
/// token. Without this, a fast cancel could race past the slow start.</para>
///
/// <para>Once Plan B (V2 wire contract with first-class CT) lands, this
/// registry can be retired or re-purposed as the bridge between wire CT
/// and per-ticket internal CT.</para>
/// </summary>
public sealed class ScriptCancellationRegistry
{
    /// <summary>Marker placeholder for "Cancel arrived before GetOrCreate".</summary>
    private static readonly CancellationTokenSource EarlyCancelledSentinel
        = CreateEarlyCancelledSentinel();

    private static CancellationTokenSource CreateEarlyCancelledSentinel()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts;
    }

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the <see cref="CancellationToken"/> for the given ticket. Idempotent —
    /// concurrent calls for the same ticket share a single CTS. If a
    /// <see cref="Cancel"/> arrived earlier (out-of-order RPC), the returned
    /// token is already-cancelled.
    /// </summary>
    public CancellationToken GetOrCreate(ScriptTicket ticket)
    {
        var key = ticket.TaskId;
        var cts = _entries.GetOrAdd(key, _ => new CancellationTokenSource());

        // If GetOrAdd returned the early-cancelled sentinel (Cancel arrived first),
        // we've handed back the sentinel's token which is already cancelled.
        // The eventual Cleanup keyed on the same TaskId will leave the sentinel
        // in place — that's deliberate: a re-registered ticket post-cleanup
        // should NOT re-pick up the sentinel; Cleanup drops the entry.
        return cts.Token;
    }

    /// <summary>
    /// Cancels the per-ticket CTS, propagating to any in-flight async work
    /// using the token. No-op if the ticket was never registered (the
    /// out-of-order case installs an early-cancelled placeholder so the
    /// eventual <see cref="GetOrCreate"/> still observes the cancellation).
    /// </summary>
    public void Cancel(ScriptTicket ticket)
    {
        var key = ticket.TaskId;

        // Out-of-order defence: if no entry yet, install the early-cancelled
        // sentinel so the eventual GetOrCreate observes the cancellation.
        // We use AddOrUpdate to handle the race where GetOrCreate fires
        // between our TryGetValue and Add.
        // Out-of-order defence: if no entry yet, install the early-cancelled
        // sentinel so the eventual GetOrCreate observes the cancellation.
        // We use AddOrUpdate to handle the race where GetOrCreate fires
        // between our TryGetValue and Add.
        _entries.AddOrUpdate(
            key,
            addValueFactory: _ => EarlyCancelledSentinel,
            updateValueFactory: (_, existing) =>
            {
                // Existing entry — flip its cancellation. Don't replace it
                // with the sentinel; the original CTS may have other waiters
                // attached via Token.Register.
                if (!ReferenceEquals(existing, EarlyCancelledSentinel))
                {
                    try { existing.Cancel(); } catch (ObjectDisposedException) { /* race with Cleanup */ }
                }
                return existing;
            });
    }

    /// <summary>
    /// Disposes the per-ticket CTS and removes the entry. Called from
    /// <see cref="LocalScriptService.CompleteScript"/> when the script
    /// reaches a terminal state. No-op if the ticket was never registered
    /// or was already cleaned up.
    /// </summary>
    public void Cleanup(ScriptTicket ticket)
    {
        var key = ticket.TaskId;
        if (!_entries.TryRemove(key, out var cts)) return;

        // The early-cancelled sentinel is shared — never dispose it.
        if (ReferenceEquals(cts, EarlyCancelledSentinel)) return;

        try { cts.Dispose(); } catch (ObjectDisposedException) { /* idempotent */ }
    }
}
