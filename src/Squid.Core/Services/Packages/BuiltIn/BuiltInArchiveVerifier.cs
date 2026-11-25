using Squid.Core.Services.Publishing;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>What the push learned from its one pass over the archive: what the version steps need, and the facts for the index row.</summary>
internal sealed record VerifiedArchive(PackageArchiveInspection Inspection, PackageFacts Facts);

/// <summary>Makes the one pass over a pushed archive and reads the package facts off what it collected.</summary>
internal interface IBuiltInArchiveVerifier : IScopedDependency
{
    /// <summary>
    /// Refuses an unsafe or too-large archive (<c>PackageArchiveRejectedException</c>, <c>PackageArchiveLimitExceededException</c>) before anything else looks at it. Only then do the
    /// package inspectors run, on what the same walk collected, so they never see an archive that has not passed the limits, and one that fails does not fail the push.
    /// </summary>
    Task<VerifiedArchive> VerifyAsync(string path, BuiltInPackageFormat format, string packageId, CancellationToken ct);
}

internal sealed class BuiltInArchiveVerifier(IPackageInspectionService inspection) : IBuiltInArchiveVerifier
{
    public async Task<VerifiedArchive> VerifyAsync(string path, BuiltInPackageFormat format, string packageId, CancellationToken ct)
    {
        var walked = PackageArchiveInspector.Inspect(path, format, packageId, ArchiveLimits.Default, inspection.CreateScanner(), ct);
        var facts = await inspection.InspectAsync(path, format, walked.Scan, ct).ConfigureAwait(false);

        return new VerifiedArchive(walked, facts);
    }
}
