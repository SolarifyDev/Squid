using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Message.Commands.BuiltInPackages;

namespace Squid.Core.Services.Packages.BuiltIn;

public interface IBuiltInPackagePushService : IScopedDependency
{
    /// <summary>
    /// Stores the pushed archive in the feed's package store and indexes it as a new version. When the package's latest version already holds the same
    /// content (the same files, however they are packed), returns that version (<c>Deduplicated</c>) and stores nothing, takes no version number. Every refusal is a specific exception.
    /// </summary>
    Task<PushBuiltInPackageResponseData> PushAsync(PushBuiltInPackageCommand command, CancellationToken ct);
}

// Flat pipeline: each step enriches _ctx or throws its own exception; a push whose content the latest version already holds leaves through
// PackageContentUnchangedException, which is caught here and answered as a success.
internal sealed partial class BuiltInPackagePushService(IBuiltInFeedLocator feeds, IBuiltInPackageDataProvider packages, IBuiltInVersionResolver versions, IBuiltInBlobWriter blobs, IBuiltInPackageRecorder recorder) : IBuiltInPackagePushService
{
    private PushContext _ctx;

    public async Task<PushBuiltInPackageResponseData> PushAsync(PushBuiltInPackageCommand command, CancellationToken ct)
    {
        _ctx = new PushContext(command);

        try
        {
            await LoadFeedAsync(ct);
            ResolvePackageId();
            await StageUploadAsync(ct);
            DetectArchiveFormat();
            InspectArchive();
            await UseCanonicalPackageIdAsync(ct);
            await EnsureNotDuplicateOfLatestAsync(ct);
            await ResolveVersionAsync(ct);
            await EnsureVersionWritableAsync(ct);
            await WriteBlobAsync(ct);
            await RecordVersionAsync(ct);

            return BuildResponse(_ctx.Recorded, deduplicated: false);
        }
        catch (PackageContentUnchangedException unchanged)
        {
            return BuildResponse(unchanged.Existing, deduplicated: true);
        }
        finally
        {
            DeleteStagedFile();
        }
    }
}
