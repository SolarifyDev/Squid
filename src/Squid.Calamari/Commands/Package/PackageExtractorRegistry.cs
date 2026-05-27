namespace Squid.Calamari.Commands.Package;

/// <summary>
/// Multi-format archive dispatch (PR-2). Iterates registered
/// <see cref="IPackageExtractor"/>s and returns the first one that
/// <see cref="IPackageExtractor.CanHandle"/>s the archive — by filename
/// extension. Order matters for multi-part extensions:
/// <list type="number">
///   <item><see cref="TarGzPackageExtractor"/> first — needs to match
///         <c>.tar.gz</c> / <c>.tgz</c> compound suffix.</item>
///   <item><see cref="ZipPackageExtractor"/> — <c>.zip</c> / <c>.nupkg</c>.</item>
///   <item><see cref="TarPackageExtractor"/> — plain <c>.tar</c>.</item>
///   <item><see cref="SevenZipUnsupportedExtractor"/> — recognised but
///         deliberately failing extractor for <c>.7z</c> (deferred dep).</item>
/// </list>
///
/// <para>If none match, <see cref="Resolve"/> returns null — caller
/// (<see cref="ExtractPackageStep"/>) treats this as "unsupported
/// extension" and throws with operator-actionable guidance.</para>
/// </summary>
internal static class PackageExtractorRegistry
{
    /// <summary>Ordered list — compound suffixes BEFORE single-part suffixes.</summary>
    private static readonly IReadOnlyList<IPackageExtractor> Extractors = new IPackageExtractor[]
    {
        new TarGzPackageExtractor(),
        new ZipPackageExtractor(),
        new TarPackageExtractor(),
        new SevenZipUnsupportedExtractor()
    };

    /// <summary>List of file-extension-style strings the dispatcher
    /// recognises. Used in error messages so operators get a complete
    /// "supported formats: …" list when they hit an unsupported extension.</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } = new[]
    {
        ".zip",
        ".nupkg",
        ".tar",
        ".tar.gz",
        ".tgz"
    };

    public static IPackageExtractor? Resolve(string archivePath)
        => Extractors.FirstOrDefault(e => e.CanHandle(archivePath));
}
