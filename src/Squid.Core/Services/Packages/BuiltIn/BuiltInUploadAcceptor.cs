using Squid.Message.Commands.BuiltInPackages;
using Squid.Message.Constants;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>A pushed file that has been staged, recognised and verified: the temp file, its format, what the one pass over it learned, and the package facts.</summary>
internal sealed record AcceptedUpload(StagedUpload Staged, BuiltInPackageFormat Format, PackageArchiveInspection Inspection, PackageFacts Facts);

/// <summary>Takes in a pushed file: stages it to disk, tells its format, and verifies it in one pass. Nothing later trusts an archive this has not accepted.</summary>
internal interface IBuiltInUploadAcceptor : IScopedDependency
{
    /// <summary>
    /// The staged file stays on disk for the caller, who removes it. When accepting fails (too large, not a supported format, unsafe, over the limits) the staged file is
    /// removed here, because the caller was never given its path.
    /// </summary>
    Task<AcceptedUpload> AcceptAsync(PushBuiltInPackageCommand command, string packageId, CancellationToken ct);
}

internal sealed class BuiltInUploadAcceptor(IBuiltInArchiveVerifier verifier) : IBuiltInUploadAcceptor
{
    public async Task<AcceptedUpload> AcceptAsync(PushBuiltInPackageCommand command, string packageId, CancellationToken ct)
    {
        var staged = await UploadStager.StageAsync(command.Content, BuiltInFeed.MaxPackageSizeBytes, command.DeclaredSizeBytes, ct).ConfigureAwait(false);

        try
        {
            var format = BuiltInPackageFormats.Detect(staged.Path);
            var verified = await verifier.VerifyAsync(staged.Path, format, packageId, ct).ConfigureAwait(false);

            return new AcceptedUpload(staged, format, verified.Inspection, verified.Facts);
        }
        catch
        {
            File.Delete(staged.Path);

            throw;
        }
    }
}
