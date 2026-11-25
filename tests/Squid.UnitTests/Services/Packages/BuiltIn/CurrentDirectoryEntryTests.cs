using System.IO;
using System.Linq;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.Http;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;

namespace Squid.UnitTests.Services.Packages.BuiltIn;

/// <summary>Archives made with <c>tar czf pkg.tgz .</c> name every entry <c>./x</c>: that resolves inside the extraction root, so it is accepted and read as <c>x</c>, while real escapes stay refused.</summary>
public class CurrentDirectoryEntryTests : IDisposable
{
    private readonly List<string> _files = new();

    public void Dispose() => _files.ForEach(File.Delete);

    private string Write(byte[] bytes)
    {
        var path = Path.GetTempFileName();

        File.WriteAllBytes(path, bytes);
        _files.Add(path);

        return path;
    }

    private static byte[] TarGzWithDirs() => TestArchives.TarGz(("./", ""), ("./index.html", "<p/>"), ("./assets/app.yaml", "a: 1"), ("./package.json", "{\"version\":\"4.5.6\"}"));

    [Theory]
    [InlineData("./", "")]
    [InlineData(".", "")]
    [InlineData("./index.html", "index.html")]
    [InlineData("././a/b", "a/b")]
    [InlineData(".\\a", "a")]
    [InlineData("a/./b", "a/./b")]
    [InlineData("..", "..")]
    [InlineData(".hidden", ".hidden")]
    public void StripLeadingCurrentDirectory_OnlyDropsTheLeadingDotSlash(string name, string expected)
    {
        PackageArchiveSafety.StripLeadingCurrentDirectory(name).ShouldBe(expected);
    }

    [Fact]
    public void Safety_AcceptsDotSlashEntries_InZipAndTarGz()
    {
        foreach (var archive in new[] { TarGzWithDirs(), TestArchives.Zip(("./index.html", "x"), ("./assets/app.yaml", "y")) })
        {
            using var stream = new MemoryStream(archive);

            Should.NotThrow(() => PackageArchiveSafety.EnsureArchiveEntriesAreSafe(stream, "p", "1"));
            Should.NotThrow(() => PackageArchiveSafety.EnsureArchiveEntriesAreSafe(archive, "p", "1"));
        }
    }

    [Theory]
    [InlineData("./../evil")]
    [InlineData("a/../../evil")]
    [InlineData("../evil")]
    [InlineData("/etc/x")]
    [InlineData("C:/x")]
    [InlineData("~/x")]
    [InlineData(".//etc/x")]
    public void Safety_StillRejectsEscapes_InZipAndTarGz(string name)
    {
        foreach (var archive in new[] { TestArchives.TarGz(("ok.txt", "x"), (name, "y")), TestArchives.Zip(("ok.txt", "x"), (name, "y")) })
        {
            using var stream = new MemoryStream(archive);

            Should.Throw<InvalidOperationException>(() => PackageArchiveSafety.EnsureArchiveEntriesAreSafe(stream, "p", "1")).Message.ShouldContain("escape");
        }
    }

    [Fact]
    public void Extractor_GivesDotSlashEntriesTheSameNamesAsUnprefixedOnes_ForTarGzAndZip()
    {
        var prefixedTar = PackageArchiveExtractor.Extract(TestArchives.TarGz(("./pkg/values.yaml", "a: 1"), ("./pkg/sub/b.json", "{}")));
        var plainTar = PackageArchiveExtractor.Extract(TestArchives.TarGz(("pkg/values.yaml", "a: 1"), ("pkg/sub/b.json", "{}")));
        var prefixedZip = PackageArchiveExtractor.Extract(TestArchives.Zip(("./pkg/values.yaml", "a: 1"), ("./pkg/sub/b.json", "{}")));
        var plainZip = PackageArchiveExtractor.Extract(TestArchives.Zip(("pkg/values.yaml", "a: 1"), ("pkg/sub/b.json", "{}")));

        prefixedTar.Keys.OrderBy(k => k).ShouldBe(plainTar.Keys.OrderBy(k => k));
        prefixedZip.Keys.OrderBy(k => k).ShouldBe(plainZip.Keys.OrderBy(k => k));
        prefixedTar.Keys.OrderBy(k => k).ShouldBe(["sub/b.json", "values.yaml"]);
    }

    [Fact]
    public void Extractor_RootFilesOfADotSlashArchive_AreUnprefixed()
    {
        PackageArchiveExtractor.Extract(TarGzWithDirs()).Keys.OrderBy(k => k).ShouldBe(["app.yaml", "package.json"]); // assets/ is the wrapper directory the extractor has always stripped
        PackageArchiveExtractor.Extract(TestArchives.Zip(("./package.json", "{}"), ("./values.yaml", "a: 1"))).Keys.OrderBy(k => k).ShouldBe(["package.json", "values.yaml"]);
    }

    [Fact]
    public void ExternalFeedPackage_WithDotSlashEntries_ExtractsTheSameNamesThroughTheHttpSource()
    {
        var bytes = TestArchives.TarGz(("./Chart.yaml", "name: x"), ("./values.yaml", "a: 1"));
        var viaPlain = PackageArchiveExtractor.Extract(TestArchives.TarGz(("Chart.yaml", "name: x"), ("values.yaml", "a: 1")));

        PackageArchiveExtractor.Extract(bytes).Keys.OrderBy(k => k).ShouldBe(viaPlain.Keys.OrderBy(k => k));
        PackageArchiveExtractor.Extract(bytes).Keys.OrderBy(k => k).ShouldBe(["Chart.yaml", "values.yaml"]);
    }

    [Theory]
    [InlineData(BuiltInPackageFormat.Zip)]
    [InlineData(BuiltInPackageFormat.TarGz)]
    public void TextReader_FindsDotSlashManifests_AndListsThemAtTheRoot(BuiltInPackageFormat format)
    {
        var entries = new[] { ("./", ""), ("./package.json", "{\"version\":\"4.5.6\"}"), ("./src/x.txt", "x") };
        var path = Write(format == BuiltInPackageFormat.Zip ? TestArchives.Zip(entries) : TestArchives.TarGz(entries));
        var reader = new PackageArchiveTextReader(path, format);

        reader.TryReadTextFileAsync("package.json", CancellationToken.None).Result.ShouldBe("{\"version\":\"4.5.6\"}");
        reader.ListRootFileNamesAsync(CancellationToken.None).Result.ShouldBe(["package.json"]);
    }

    [Fact]
    public async Task Push_OfATarMadeWithTarCzfDot_IsAccepted_AndSourceVersionReadsItsManifest()
    {
        var h = new PushHarness { Rules = new FakeRules("{source.version}") };

        var result = await h.Service().PushAsync(PushHarness.Push(TarGzWithDirs()), CancellationToken.None);

        result.Package.Version.ShouldBe("4.5.6");
        result.Package.FileExtension.ShouldBe(".tar.gz");
    }
}
