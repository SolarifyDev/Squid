namespace Squid.Core.Services.Packages.BuiltIn;

internal sealed partial class BuiltInPackagePushService
{
    private async Task ResolveVersionAsync(CancellationToken ct)
    {
        var archive = new PackageArchiveTextReader(_ctx.Staged.Path, _ctx.Format, _ctx.Inspection);

        _ctx.Version = await versions.ResolveAsync(_ctx.Feed, _ctx.PackageId, _ctx.Command, archive, ct).ConfigureAwait(false);
    }

    private async Task EnsureVersionWritableAsync(CancellationToken ct)
    {
        _ctx.Replacing = await recorder.EnsureWritableAsync(_ctx.Feed.Id, _ctx.PackageId, _ctx.Version, _ctx.Command.Replace, ct).ConfigureAwait(false);
    }
}
