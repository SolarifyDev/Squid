using Squid.Core.Services.Packages.BuiltIn.Exceptions;

namespace Squid.Core.Services.Packages.BuiltIn;

internal sealed partial class BuiltInPackagePushService
{
    private async Task LoadFeedAsync(CancellationToken ct)
    {
        _ctx.Feed = await catalog.GetFeedAsync(_ctx.Command.SpaceId, ct).ConfigureAwait(false);
    }

    private void ResolvePackageId()
    {
        _ctx.PackageId = BuiltInPackageIds.Resolve(_ctx.Command.PackageId, _ctx.Command.FileName);
    }

    // Stage, recognise and verify the file in one pass (unsafe names, the decompression limits, the content fingerprint, the root manifests and the inspection scan).
    // The facts are read off that pass afterwards, in memory, and never fail the push.
    private async Task AcceptUploadAsync(CancellationToken ct)
    {
        var accepted = await acceptor.AcceptAsync(_ctx.Command, _ctx.PackageId, ct).ConfigureAwait(false);

        _ctx.Staged = accepted.Staged;
        _ctx.Format = accepted.Format;
        _ctx.Inspection = accepted.Inspection;
        _ctx.Facts = accepted.Facts;
    }

    // After the checks above, so a push that is refused never fixes a package's casing. From here on the package id is the feed's canonical one.
    private async Task UseCanonicalPackageIdAsync(CancellationToken ct)
    {
        _ctx.PackageId = await catalog.ResolveCanonicalIdAsync(_ctx.Feed.Id, _ctx.PackageId, ct).ConfigureAwait(false);
    }

    // Before any version is assigned: re-pushing the same content must not use up a version number. It never swallows a request for a version of its own: a version
    // the caller named is deduplicated only when it is the latest one, and a replace is a request to write, not to look.
    private async Task EnsureNotDuplicateOfLatestAsync(CancellationToken ct)
    {
        if (_ctx.Command.Replace) return;

        var latest = await catalog.GetLatestAsync(_ctx.Feed.Id, _ctx.PackageId, ct).ConfigureAwait(false);

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
