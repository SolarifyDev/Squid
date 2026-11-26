using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Message.Constants;
using Squid.Message.Enums.BuiltInPackages;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>Everything the index row of a push is made of.</summary>
internal sealed record PackageRecording(ExternalFeed Feed, string PackageId, string Version, StagedUpload Staged, BuiltInPackageFormat Format, string ContentFingerprint, StoredBlob Blob, BuiltInPackageSource Source, BuiltInPackage Replacing);

/// <summary>Decides whether a push may write its version, and writes the index row.</summary>
internal interface IBuiltInPackageRecorder : IScopedDependency
{
    /// <summary>
    /// Null when the version is new, the existing row when it may be replaced. Throws <c>PackageVersionAlreadyExistsException</c> when it exists and <paramref name="replace"/> is not set,
    /// and <c>PackageVersionInUseException</c> when it is set but a release selects the version: a release is a promise that its version's bytes stay as they were.
    /// </summary>
    Task<BuiltInPackage> EnsureWritableAsync(int feedId, string packageId, string version, bool replace, CancellationToken ct);

    Task<BuiltInPackage> RecordAsync(PackageRecording recording, CancellationToken ct);
}

internal sealed class BuiltInPackageRecorder(IBuiltInPackageDataProvider packages, IBuiltInPackageUsage usage, ICurrentUser currentUser, TimeProvider time = null) : IBuiltInPackageRecorder
{
    public async Task<BuiltInPackage> EnsureWritableAsync(int feedId, string packageId, string version, bool replace, CancellationToken ct)
    {
        var existing = await packages.GetAsync(feedId, packageId, version, ct).ConfigureAwait(false);

        if (existing == null) return null;

        if (!replace)
            throw new PackageVersionAlreadyExistsException(packageId, version);

        // An early answer, so a push that cannot succeed fails before it writes a file. The row update checks again, atomically, because a release can select the
        // version while a long file write is going on.
        if (await usage.IsVersionSelectedByReleaseAsync(feedId, packageId, version, ct).ConfigureAwait(false))
            throw new PackageVersionInUseException(packageId, version);

        return existing;
    }

    public async Task<BuiltInPackage> RecordAsync(PackageRecording recording, CancellationToken ct)
    {
        var row = BuildRow(recording);

        if (recording.Replacing == null)
        {
            await packages.InsertAsync(row, ct).ConfigureAwait(false);

            return row;
        }

        row.Id = recording.Replacing.Id;

        await packages.ReplaceContentAsync(row.Id, row, ct).ConfigureAwait(false);

        return row;
    }

    private BuiltInPackage BuildRow(PackageRecording r)
    {
        return new BuiltInPackage
        {
            SpaceId = r.Feed.SpaceId,
            FeedId = r.Feed.Id,
            PackageId = r.PackageId,
            Version = r.Version,
            FileExtension = BuiltInPackageFormats.ExtensionOf(r.Format),
            SizeBytes = r.Staged.SizeBytes,
            Sha256 = r.Staged.Sha256,
            ContentFingerprint = r.ContentFingerprint,
            StoreId = r.Blob.StoreId,
            BlobKey = r.Blob.Key,
            PublishedDate = (time ?? TimeProvider.System).GetUtcNow(),
            PublishedBy = currentUser?.Id ?? CurrentUsers.InternalUser.Id,
            Source = r.Source.ToString().ToLowerInvariant()
        };
    }
}
