namespace Squid.Message.Enums.OctopusImport;

/// <summary>
/// Where a running confirmation stands. Append-only: the SquidWeb mirror is ordinal. A run goes <see cref="Validating"/> →
/// <see cref="WaitingForSpaceLock"/> → <see cref="Importing"/> → <see cref="FillingSecrets"/> → <see cref="RecordingSourceMap"/> →
/// <see cref="Committing"/>, then ends <see cref="Succeeded"/>, <see cref="Failed"/> or <see cref="RolledBack"/>.
/// </summary>
public enum OctopusImportConfirmationStage
{
    Validating = 0,
    WaitingForSpaceLock = 1,
    Importing = 2,
    FillingSecrets = 3,
    RecordingSourceMap = 4,
    Committing = 5,
    Succeeded = 6,
    Failed = 7,

    /// <summary>The attempt imported nothing; the session is Validated again and can be imported again.</summary>
    RolledBack = 8
}
