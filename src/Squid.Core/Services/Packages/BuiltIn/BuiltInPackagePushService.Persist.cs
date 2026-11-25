using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Core.Services.Publishing;
using Squid.Message.Commands.BuiltInPackages;
using Squid.Message.Constants;

namespace Squid.Core.Services.Packages.BuiltIn;

public sealed partial class BuiltInPackagePushService
{
    // Facts are advisory: inspection never throws, so a package nobody can read facts from is still pushed, with the failure recorded as a fact.
    private async Task InspectPackageAsync(CancellationToken ct)
    {
        _ctx.Facts = await inspection.InspectAsync(_ctx.Staged.Path, _ctx.Format, ct).ConfigureAwait(false);
    }

    // The feed's StoreId property picks the store; without one the default store is used, and its id is recorded either way so the package stays findable
    // after the default moves.
    private async Task ResolveStoreAsync(CancellationToken ct)
    {
        _ctx.StoreId = await ReadStoreIdAsync(ct).ConfigureAwait(false);
        _ctx.Store = await registry.ResolveAsync(_ctx.StoreId, ct).ConfigureAwait(false);
    }

    private async Task<int> ReadStoreIdAsync(CancellationToken ct)
    {
        var configured = ExternalFeedProperties.GetString(_ctx.Feed, BuiltInFeed.StoreIdProperty);

        if (string.IsNullOrWhiteSpace(configured))
            return (await stores.GetDefaultAsync(ct).ConfigureAwait(false))?.Id ?? throw new NoDefaultPackageStoreException();

        return int.TryParse(configured, out var storeId) ? storeId : throw new BuiltInFeedStoreInvalidException(_ctx.Feed.Id, configured);
    }

    // Content addressed: when the store already holds these bytes under this key (another version or package pushed the same file) nothing is written.
    private async Task WriteBlobAsync(CancellationToken ct)
    {
        _ctx.BlobKey = BuiltInBlobKey.For(_ctx.Staged.Sha256, BuiltInPackageFormats.ExtensionOf(_ctx.Format));

        if (await _ctx.Store.ExistsAsync(_ctx.BlobKey, ct).ConfigureAwait(false)) return;

        await using var file = File.OpenRead(_ctx.Staged.Path);

        await _ctx.Store.PutAsync(_ctx.BlobKey, file, ct).ConfigureAwait(false);
    }

    private async Task RecordVersionAsync(CancellationToken ct)
    {
        _ctx.Recorded = BuildRow();

        if (_ctx.Replacing == null)
        {
            await packages.InsertAsync(_ctx.Recorded, ct).ConfigureAwait(false);
            return;
        }

        _ctx.Recorded.Id = _ctx.Replacing.Id;

        await packages.ReplaceContentAsync(_ctx.Replacing.Id, _ctx.Recorded, ct).ConfigureAwait(false);
    }

    private BuiltInPackage BuildRow()
    {
        return new BuiltInPackage
        {
            SpaceId = _ctx.Feed.SpaceId,
            FeedId = _ctx.Feed.Id,
            PackageId = _ctx.PackageId,
            Version = _ctx.Version,
            FileExtension = BuiltInPackageFormats.ExtensionOf(_ctx.Format),
            SizeBytes = _ctx.Staged.SizeBytes,
            Sha256 = _ctx.Staged.Sha256,
            StoreId = _ctx.StoreId,
            BlobKey = _ctx.BlobKey,
            PublishedDate = (time ?? TimeProvider.System).GetUtcNow(),
            PublishedBy = currentUser?.Id ?? CurrentUsers.InternalUser.Id,
            Source = _ctx.Command.Source.ToString().ToLowerInvariant(),
            Facts = PackageFactsJson.Serialize(_ctx.Facts)
        };
    }

    private PushBuiltInPackageResponseData BuildResponse(BuiltInPackage row, bool deduplicated)
    {
        return new PushBuiltInPackageResponseData { Package = BuiltInPackageMapper.ToDto(row, withFacts: true), Deduplicated = deduplicated, Replaced = !deduplicated && _ctx.Replacing != null };
    }

    private void DeleteStagedFile()
    {
        if (_ctx?.Staged != null) File.Delete(_ctx.Staged.Path);
    }
}
