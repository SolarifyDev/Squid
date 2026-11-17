namespace Squid.Core.Persistence.Entities.Deployments;

public class OctopusImportSession : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public Guid SessionId { get; set; }

    public int DestinationSpaceId { get; set; }

    public int OwnerUserId { get; set; }

    public string State { get; set; }

    public string SourceSummaryJson { get; set; }

    public string RedactedNormalizedDataJson { get; set; }

    public string ValidatedPlanJson { get; set; }

    public string ResultJson { get; set; }

    public string TemporaryUploadPath { get; set; }

    public long? TemporaryUploadSizeBytes { get; set; }

    public DateTimeOffset? TemporaryUploadCleanupAfter { get; set; }

    public DateTimeOffset? TemporaryUploadCleanedAt { get; set; }

    public string TemporaryUploadCleanupError { get; set; }

    public string ProtectedExportPassword { get; set; }

    public byte[] DataVersion { get; set; }

    /// <summary>The confirmation attempt that holds an Importing session; every write that ends the attempt is fenced on it.</summary>
    public Guid? ConfirmationAttemptId { get; set; }

    /// <summary>Refreshed by the running confirmation on its own connection; never changes <see cref="DataVersion"/>.</summary>
    public DateTimeOffset? ImportHeartbeatAt { get; set; }

    public string LastConfirmationFailureJson { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset LastStateChangedAt { get; set; }

    public DateTimeOffset CreatedDate { get; set; }

    public int CreatedBy { get; set; }

    public DateTimeOffset LastModifiedDate { get; set; }

    public int LastModifiedBy { get; set; }
}
