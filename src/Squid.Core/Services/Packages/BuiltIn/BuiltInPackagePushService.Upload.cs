using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Message.Constants;

namespace Squid.Core.Services.Packages.BuiltIn;

internal sealed partial class BuiltInPackagePushService
{
    private async Task LoadFeedAsync(CancellationToken ct)
    {
        _ctx.Feed = await feeds.GetAsync(_ctx.Command.SpaceId, ct).ConfigureAwait(false);
    }

    private void ResolvePackageId()
    {
        _ctx.PackageId = BuiltInPackageIds.Resolve(_ctx.Command.PackageId, _ctx.Command.FileName);
    }

    private async Task StageUploadAsync(CancellationToken ct)
    {
        _ctx.Staged = await UploadStager.StageAsync(_ctx.Command.Content, BuiltInFeed.MaxPackageSizeBytes, _ctx.Command.DeclaredSizeBytes, ct).ConfigureAwait(false);
    }

    private void DetectArchiveFormat()
    {
        _ctx.Format = BuiltInPackageFormats.Detect(_ctx.Staged.Path);
    }

    // One walk: unsafe names, the decompression limits, the content fingerprint, the root manifests and the inspection scan, so the archive is not unpacked again for
    // each of them. The facts are read off that scan afterwards, in memory, and never fail the push.
    private async Task VerifyArchiveAsync(CancellationToken ct)
    {
        var verified = await verifier.VerifyAsync(_ctx.Staged.Path, _ctx.Format, _ctx.PackageId, ct).ConfigureAwait(false);

        _ctx.Inspection = verified.Inspection;
        _ctx.Facts = verified.Facts;
    }

    // After the checks above, so a push that is refused never fixes a package's casing. From here on the package id is the feed's canonical one.
    private async Task UseCanonicalPackageIdAsync(CancellationToken ct)
    {
        _ctx.PackageId = await packages.ResolveCanonicalIdAsync(_ctx.Feed.Id, _ctx.PackageId, ct).ConfigureAwait(false);
    }

    // Before any version is assigned: re-pushing the same content must not use up a version number. It never swallows a request for a version of its own: a version
    // the caller named is deduplicated only when it is the latest one, and a replace is a request to write, not to look.
    private async Task EnsureNotDuplicateOfLatestAsync(CancellationToken ct)
    {
        if (_ctx.Command.Replace) return;

        var existing = await packages.ListVersionsAsync(_ctx.Feed.Id, _ctx.PackageId, ct).ConfigureAwait(false);
        var latestVersion = PackageVersionFilter.SortByVersionDescending(existing.Select(v => v.Version)).FirstOrDefault();
        var latest = existing.FirstOrDefault(v => v.Version == latestVersion);

        if (!HoldsTheSameContent(latest) || NamesAnotherVersion(latest)) return;

        throw new PackageContentUnchangedException(latest);
    }

    // A row pushed before fingerprints existed has none and never matches.
    private bool HoldsTheSameContent(Persistence.Entities.Deployments.BuiltInPackage latest)
    {
        return latest?.ContentFingerprint != null && latest.ContentFingerprint == _ctx.Inspection.ContentFingerprint;
    }

    private bool NamesAnotherVersion(Persistence.Entities.Deployments.BuiltInPackage latest)
    {
        return !string.IsNullOrWhiteSpace(_ctx.Command.Version) && _ctx.Command.Version.Trim() != latest.Version;
    }
}
