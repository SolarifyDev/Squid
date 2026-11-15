namespace Squid.Message.Enums;

/// <summary>
/// The state of a secret a caller wants to fill only when it is still empty. A read reports <see cref="Empty"/>, <see cref="AlreadySet"/>,
/// <see cref="NotFound"/> or <see cref="Ambiguous"/>; a fill reports <see cref="Filled"/> or why it wrote nothing.
/// </summary>
public enum SecretSlotState
{
    Empty = 0,

    /// <summary>A value is stored: a fill never overwrites it.</summary>
    AlreadySet = 1,

    /// <summary>No secret with that identity exists (renamed, re-scoped or deleted).</summary>
    NotFound = 2,

    /// <summary>More than one secret has that identity; nothing is written, the user sets it.</summary>
    Ambiguous = 3,

    /// <summary>The empty value was filled.</summary>
    Filled = 4
}
