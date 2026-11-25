using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.BuiltInPackages;

namespace Squid.Core.Services.Packages.BuiltIn;

internal sealed partial class BuiltInPackagePushService
{
    private async Task WriteBlobAsync(CancellationToken ct)
    {
        _ctx.Blob = await blobs.WriteAsync(_ctx.Feed, _ctx.Staged, _ctx.Format, ct).ConfigureAwait(false);
    }

    private async Task RecordVersionAsync(CancellationToken ct)
    {
        var recording = new PackageRecording(_ctx.Feed, _ctx.PackageId, _ctx.Version, _ctx.Staged, _ctx.Format, _ctx.Inspection.ContentFingerprint, _ctx.Blob, _ctx.Command.Source, _ctx.Replacing, _ctx.Facts);

        _ctx.Recorded = await recorder.RecordAsync(recording, ct).ConfigureAwait(false);
    }

    private PushBuiltInPackageResponseData BuildResponse(BuiltInPackage row, bool deduplicated)
    {
        return new PushBuiltInPackageResponseData { Package = BuiltInPackageMapper.ToDto(row, withFacts: true), Deduplicated = deduplicated, Replaced = !deduplicated && _ctx.Replacing != null };
    }

    // Runs in finally: a file that cannot be removed is logged, never allowed to replace the push's own outcome (or its exception) with an IO error.
    private void DeleteStagedFile()
    {
        if (_ctx?.Staged == null) return;

        try
        {
            File.Delete(_ctx.Staged.Path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BuiltInFeed] Could not remove the staged upload {Path}", _ctx.Staged.Path);
        }
    }
}
