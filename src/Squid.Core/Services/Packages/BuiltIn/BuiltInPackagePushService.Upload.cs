using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Message.Constants;

namespace Squid.Core.Services.Packages.BuiltIn;

public sealed partial class BuiltInPackagePushService
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

    private void EnsureArchiveSafe()
    {
        try
        {
            using var file = File.OpenRead(_ctx.Staged.Path);

            PackageArchiveSafety.EnsureArchiveEntriesAreSafe(file, _ctx.PackageId, "being pushed");
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            throw new PackageArchiveRejectedException(ex.Message, ex);
        }
    }

    // After the checks above, so a push that is refused never fixes a package's casing. From here on the package id is the feed's canonical one.
    private async Task UseCanonicalPackageIdAsync(CancellationToken ct)
    {
        _ctx.PackageId = await packages.ResolveCanonicalIdAsync(_ctx.Feed.Id, _ctx.PackageId, ct).ConfigureAwait(false);
    }

    // Before any version is assigned: re-pushing identical bytes must not use up a version number.
    private async Task EnsureLatestDiffersAsync(CancellationToken ct)
    {
        var versions = await packages.ListVersionsAsync(_ctx.Feed.Id, _ctx.PackageId, ct).ConfigureAwait(false);
        var latest = PackageVersionFilter.SortByVersionDescending(versions.Select(v => v.Version)).FirstOrDefault();
        var latestRow = versions.FirstOrDefault(v => v.Version == latest);

        if (latestRow != null && latestRow.Sha256 == _ctx.Staged.Sha256)
            throw new PackageContentUnchangedException(latestRow);
    }
}
