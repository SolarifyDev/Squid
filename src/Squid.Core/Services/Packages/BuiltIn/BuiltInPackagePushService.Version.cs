using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.BuiltIn;

public sealed partial class BuiltInPackagePushService
{
    // The version the caller typed is checked through the assigner like any template's result, so it must sort; otherwise the package's rule decides.
    private async Task ResolveVersionAsync(CancellationToken ct)
    {
        var template = await ChooseTemplateAsync(ct).ConfigureAwait(false);

        _ctx.Version = await assigner.AssignAsync(template, BuildTokenContext(), ct).ConfigureAwait(false);
    }

    private async Task<string> ChooseTemplateAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_ctx.Command.Version)) return InputTemplate;

        var rule = await rules.GetEffectiveRuleAsync(_ctx.Command.SpaceId, _ctx.Feed.Id, _ctx.PackageId, ct).ConfigureAwait(false);

        return rule.VersionTemplate;
    }

    private VersionTokenContext BuildTokenContext()
    {
        var reader = new PackageArchiveTextReader(_ctx.Staged.Path, _ctx.Format);

        return new VersionTokenContext
        {
            FeedId = _ctx.Feed.Id,
            PackageId = _ctx.PackageId,
            FileName = _ctx.Command.FileName,
            InputVersion = _ctx.Command.Version,
            GitSha = _ctx.Command.GitSha,
            TryReadTextFileAsync = reader.TryReadTextFileAsync,
            ListRootFileNamesAsync = reader.ListRootFileNamesAsync
        };
    }

    // An existing version is overwritten only on request and only while no release selects it: a release is a promise that its version's bytes stay as they were.
    private async Task EnsureVersionWritableAsync(CancellationToken ct)
    {
        var existing = await packages.GetAsync(_ctx.Feed.Id, _ctx.PackageId, _ctx.Version, ct).ConfigureAwait(false);

        if (existing == null) return;

        if (!_ctx.Command.Replace)
            throw new PackageVersionAlreadyExistsException(_ctx.PackageId, _ctx.Version);

        if (await usage.IsVersionSelectedByReleaseAsync(_ctx.Feed.Id, _ctx.PackageId, _ctx.Version, ct).ConfigureAwait(false))
            throw new PackageVersionInUseException(_ctx.PackageId, _ctx.Version);

        _ctx.Replacing = existing;
    }

    private const string InputTemplate = "{input}";
}
