namespace Squid.Tentacle.ScriptExecution.V2;

/// <summary>
/// A fixed pool of lock objects keyed by ticket, so V2 Start/Cancel/Complete for one ticket serialise
/// without a lock-per-ticket dictionary that would need cleanup. Two tickets may share a stripe; that only
/// costs a brief wait, never correctness.
/// </summary>
public sealed class TicketLockStripes
{
    private const int StripeCount = 64;

    private readonly object[] _stripes = Enumerable.Range(0, StripeCount).Select(_ => new object()).ToArray();

    public object For(string ticketId) => _stripes[(uint)StringComparer.Ordinal.GetHashCode(ticketId) % StripeCount];
}
