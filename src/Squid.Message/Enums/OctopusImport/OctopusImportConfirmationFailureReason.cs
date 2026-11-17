namespace Squid.Message.Enums.OctopusImport;

/// <summary>Why the last confirmation attempt of a session that is Validated again imported nothing.</summary>
public enum OctopusImportConfirmationFailureReason
{
    /// <summary>The import failed and its transaction was rolled back; nothing was written.</summary>
    RolledBack,

    /// <summary>The database kept failing transiently through every automatic retry; nothing was written.</summary>
    TransientRetriesExhausted,

    /// <summary>The server running the import stopped (its heartbeat went stale) before it committed; nothing was written.</summary>
    Interrupted,

    /// <summary>A transient database failure stopped a queued import before it started.</summary>
    NotStarted
}
