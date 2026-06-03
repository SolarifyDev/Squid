namespace Squid.Message.Models.Events;

/// <summary>
/// Structured reference arguments for a document-audit event (created / modified / deleted),
/// serialized to the Event's jsonb <c>references</c> column. The history UI renders the
/// document type + name from these without a pre-baked English string.
/// </summary>
public sealed record DocumentEventReferences
{
    public required string DocumentType { get; init; }

    public string Name { get; init; }
}
