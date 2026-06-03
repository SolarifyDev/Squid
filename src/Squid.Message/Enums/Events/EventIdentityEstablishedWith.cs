namespace Squid.Message.Enums.Events;

/// <summary>
/// How the actor's identity was established for an event — Octopus's
/// "Established with". Stored as <c>smallint</c> (low-cardinality fixed set).
/// Values are persisted; do not renumber.
/// </summary>
public enum EventIdentityEstablishedWith : short
{
    /// <summary>Background job / system, no inbound HTTP request context.</summary>
    Server = 0,

    /// <summary>Browser portal session (cookie auth).</summary>
    SessionCookie = 1,

    /// <summary>API key auth.</summary>
    ApiKey = 2,

    /// <summary>Command-line or tools.</summary>
    Cli = 3
}
