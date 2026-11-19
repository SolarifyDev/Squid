namespace Squid.Message.Enums;

/// <summary>Why a deployment cannot use a sensitive value an Octopus import recorded as missing. Persisted nowhere, but pinned: clients may switch on it.</summary>
public enum MissingImportedSecretReason
{
    /// <summary>The Octopus API withheld the value and nothing has supplied it since: set it in Squid or import the export file.</summary>
    NotImported = 0,

    /// <summary>A value has been set since the release was created, but the release's variable snapshot still holds the empty one.</summary>
    SnapshotPredatesValue = 1
}
