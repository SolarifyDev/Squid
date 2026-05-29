using Shouldly;
using Squid.Calamari.Commands.Package;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.Package;

/// <summary>
/// PR-2 — dispatch tests for <see cref="PackageExtractorRegistry"/>.
/// Confirms each extension lands on the right extractor + compound suffixes
/// resolve correctly + unsupported extensions return null (caller's signal
/// to throw with operator guidance).
/// </summary>
public sealed class PackageExtractorRegistryTests
{
    [Theory]
    [InlineData("/x/y.zip", typeof(ZipPackageExtractor))]
    [InlineData("/x/y.nupkg", typeof(ZipPackageExtractor))]
    [InlineData("/x/y.NUPKG", typeof(ZipPackageExtractor))]
    [InlineData("/x/y.tar", typeof(TarPackageExtractor))]
    [InlineData("/x/y.tar.gz", typeof(TarGzPackageExtractor))]
    [InlineData("/x/y.tgz", typeof(TarGzPackageExtractor))]
    [InlineData("/x/y.TAR.GZ", typeof(TarGzPackageExtractor))]
    [InlineData("/x/y.7z", typeof(SevenZipPackageExtractor))]
    [InlineData("/x/y.7Z", typeof(SevenZipPackageExtractor))]
    public void Resolve_KnownExtension_LandsOnExpectedExtractor(string archivePath, Type expectedType)
    {
        var extractor = PackageExtractorRegistry.Resolve(archivePath);
        extractor.ShouldNotBeNull();
        extractor.ShouldBeOfType(expectedType);
    }

    [Theory]
    [InlineData("/x/y.rar")]
    [InlineData("/x/y.gz")]    // bare .gz NOT supported (ambiguous: gzipped log vs tar.gz?)
    [InlineData("/x/y.txt")]
    [InlineData("/x/y")]
    public void Resolve_UnknownExtension_ReturnsNull(string archivePath)
    {
        PackageExtractorRegistry.Resolve(archivePath).ShouldBeNull();
    }

    [Fact]
    public void Resolve_TarGzCompoundSuffix_BeatsBareTarMatch()
    {
        // Critical ordering invariant: .tar.gz MUST dispatch to TarGz, not
        // be sliced as ".gz" or ".tar". The registry's listed order pins
        // this — TarGz is first.
        PackageExtractorRegistry.Resolve("/x/y.tar.gz")
            .ShouldBeOfType<TarGzPackageExtractor>(
                customMessage: "Compound .tar.gz MUST land on TarGzPackageExtractor (registry ordering). " +
                               "If you see this fail, .tar.gz files would either error or get treated as plain tar.");
    }

    [Fact]
    public void SupportedExtensions_ListsAllOperatorVisibleFormats()
    {
        // Pinning the operator-facing list — if a new extractor lands but
        // SupportedExtensions isn't updated, operators won't see the new
        // format in error messages.
        PackageExtractorRegistry.SupportedExtensions
            .ShouldBe(new[] { ".zip", ".nupkg", ".tar", ".tar.gz", ".tgz", ".7z" });
    }
}
