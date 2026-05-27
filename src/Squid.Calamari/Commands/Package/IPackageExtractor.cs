namespace Squid.Calamari.Commands.Package;

/// <summary>
/// Per-format archive extractor. The <see cref="ExtractPackageStep"/>
/// resolves the right extractor by filename extension via
/// <see cref="PackageExtractorRegistry"/>. Each implementation handles
/// one archive format (zip/nupkg, tar, tar.gz, …) and shares the same
/// hostile-archive defence story (zip-slip + size caps + fail-closed).
///
/// <para><b>Why per-format classes vs one big switch</b>: each format has
/// a meaningfully different parse / stream / metadata model. Tar entries
/// carry POSIX file modes that need separate handling from zip's external
/// attributes; tar.gz needs an outer GZipStream wrapper around the tar
/// reader; .7z needs an external library. Keeping them isolated lets
/// each one own its own test surface + safety review.</para>
/// </summary>
internal interface IPackageExtractor
{
    /// <summary>
    /// True if this extractor handles the archive's extension. Match by
    /// suffix — multi-part extensions like <c>.tar.gz</c> are tried before
    /// single-part <c>.gz</c> so dispatch lands on the most specific match.
    /// </summary>
    bool CanHandle(string archivePath);

    /// <summary>
    /// Extract <paramref name="archivePath"/> into <paramref name="destinationDir"/>.
    /// Returns a structured result — does not throw for predictable failure
    /// modes (zip-slip / oversize / malformed). Caller treats failure as
    /// a halt-the-deploy signal.
    /// </summary>
    ExtractResult Extract(string archivePath, string destinationDir);
}
