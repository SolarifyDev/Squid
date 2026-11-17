namespace Squid.Core.Services.OctopusImport.Exceptions;

/// <summary>
/// Fewer documents of a kind are stored than the fetch read and committed (or than were counted when the bundle build started). The bundle
/// is never built from what is left: nothing is imported. The message names the kind and the counts only, never content.
/// </summary>
public class OctopusImportHistoryStagingIncompleteException : OctopusImportHistoryStagingLostException
{
    public OctopusImportHistoryStagingIncompleteException(string kind, int stored, int expected) : base($"{DefaultMessage} {kind}: {stored} stored, {expected} read.")
    {
        Kind = kind;
        Stored = stored;
        Expected = expected;
    }

    public string Kind { get; }

    public int Stored { get; }

    public int Expected { get; }
}
