namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageNotes;

public record PackageNotesResult
{
    public bool Succeeded { get; init; }
    public string Notes { get; init; }
    public string FailureReason { get; init; }
    public DateTimeOffset? Published { get; init; }

    public static PackageNotesResult Success(string notes, DateTimeOffset? published = null) =>
        new() { Succeeded = true, Notes = notes, Published = published };

    public static PackageNotesResult Failure(string reason) =>
        new() { Succeeded = false, FailureReason = reason };

    public static PackageNotesResult Empty() =>
        new() { Succeeded = true };
}
