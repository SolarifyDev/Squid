using System.IO;
using System.Linq;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;

namespace Squid.UnitTests.Services.Packages.BuiltIn;

public class BuiltInPackageFormatsTests : IDisposable
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

    [Fact]
    public void Detect_Zip_ByItsContent()
    {
        BuiltInPackageFormats.Detect(Write(TestArchives.Zip(("a.txt", "x")))).ShouldBe(BuiltInPackageFormat.Zip);
    }

    [Fact]
    public void Detect_TarGz_ByItsContent()
    {
        BuiltInPackageFormats.Detect(Write(TestArchives.TarGz(("a.txt", "x")))).ShouldBe(BuiltInPackageFormat.TarGz);
    }

    [Theory]
    [InlineData("a text file renamed to Acme.Web.1.0.0.zip")]
    [InlineData("PK")]
    [InlineData("")]
    public void Detect_AnythingElse_IsRefused(string text)
    {
        Should.Throw<UnsupportedPackageFormatException>(() => BuiltInPackageFormats.Detect(Write(System.Text.Encoding.UTF8.GetBytes(text))));
    }

    [Fact]
    public void Detect_PlainTar_IsRefused()
    {
        Should.Throw<UnsupportedPackageFormatException>(() => BuiltInPackageFormats.Detect(Write(TestArchives.PlainTar(("a.txt", "x")))));
    }

    [Theory]
    [InlineData(true, ".zip")]
    [InlineData(false, ".tar.gz")]
    public void ExtensionOf_IsTheRealExtensionOfEachFormat(bool zip, string expected)
    {
        var format = zip ? BuiltInPackageFormat.Zip : BuiltInPackageFormat.TarGz;

        BuiltInPackageFormats.ExtensionOf(format).ShouldBe(expected);
    }

    [Fact]
    public void SafetyCheck_OverAStream_RejectsATraversalEntryInZipAndTarGz()
    {
        foreach (var evil in new[] { TestArchives.Zip(("../x", "y")), TestArchives.TarGz(("../x", "y")) })
        {
            using var stream = new MemoryStream(evil);

            Should.Throw<InvalidOperationException>(() => PackageArchiveSafety.EnsureArchiveEntriesAreSafe(stream, "p", "1")).Message.ShouldContain("escape");
        }
    }

    [Fact]
    public void SafetyCheck_OverAStream_AcceptsAnArchiveAndLeavesTheStreamOpenAndRewound()
    {
        foreach (var good in new[] { TestArchives.Zip(("a/b.txt", "y")), TestArchives.TarGz(("a/b.txt", "y")) })
        {
            using var stream = new MemoryStream(good);

            PackageArchiveSafety.EnsureArchiveEntriesAreSafe(stream, "p", "1");

            stream.CanRead.ShouldBeTrue();
            stream.Position.ShouldBe(0);
        }
    }

    [Fact]
    public void SafetyCheck_TarGzWithSeveralEntries_IsNotMistakenForAnUnsafeOne()
    {
        var archive = TestArchives.TarGz(("package.json", "{}"), ("deep/x.txt", "x"), ("pyproject.toml", "py"), ("a/b/c.txt", "zz"));

        using var stream = new MemoryStream(archive);

        Should.NotThrow(() => PackageArchiveSafety.EnsureArchiveEntriesAreSafe(stream, "p", "1"));
        Should.NotThrow(() => PackageArchiveSafety.EnsureArchiveEntriesAreSafe(archive, "p", "1"));
    }

    [Fact]
    public void SafetyCheck_TarGzWithATraversalEntryAfterOthers_IsStillRejected()
    {
        var archive = TestArchives.TarGz(("ok/a.txt", "x"), ("ok/b.txt", "y"), ("../../evil", "z"));

        using var stream = new MemoryStream(archive);

        Should.Throw<InvalidOperationException>(() => PackageArchiveSafety.EnsureArchiveEntriesAreSafe(stream, "p", "1")).Message.ShouldContain("escape");
    }

    [Fact]
    public void SafetyCheck_StreamAndByteArray_AgreeOnTheSameArchives()
    {
        foreach (var archive in new[] { TestArchives.Zip(("../x", "y")), TestArchives.TarGz(("/abs", "y")), TestArchives.Zip(("ok.txt", "y")) })
        {
            var viaBytes = Record.Exception(() => PackageArchiveSafety.EnsureArchiveEntriesAreSafe(archive, "p", "1"));
            using var stream = new MemoryStream(archive);
            var viaStream = Record.Exception(() => PackageArchiveSafety.EnsureArchiveEntriesAreSafe(stream, "p", "1"));

            viaStream?.GetType().ShouldBe(viaBytes?.GetType());
            viaStream?.Message.ShouldBe(viaBytes?.Message);
            (viaStream == null).ShouldBe(viaBytes == null);
        }
    }

    [Fact]
    public void TextReader_Zip_ReadsRootAndNestedFiles_AndListsOnlyTheRoot()
    {
        var path = Write(TestArchives.Zip(("package.json", "{\"version\":\"1.0.0\"}"), ("src/App.csproj", "nested"), ("App.csproj", "root"), ("dir/", "")));
        var reader = new PackageArchiveTextReader(path, BuiltInPackageFormat.Zip);

        reader.TryReadTextFileAsync("package.json", CancellationToken.None).Result.ShouldBe("{\"version\":\"1.0.0\"}");
        reader.TryReadTextFileAsync("src/App.csproj", CancellationToken.None).Result.ShouldBe("nested");
        reader.TryReadTextFileAsync("missing.txt", CancellationToken.None).Result.ShouldBeNull();
        reader.ListRootFileNamesAsync(CancellationToken.None).Result.OrderBy(n => n).ShouldBe(["App.csproj", "package.json"]);
    }

    [Fact]
    public void TextReader_TarGz_ReadsFilesWithOrWithoutADotSlashPrefix()
    {
        var path = Write(TestArchives.TarGz(("./package.json", "{\"version\":\"2.0.0\"}"), ("deep/x.txt", "x"), ("pyproject.toml", "py")));
        var reader = new PackageArchiveTextReader(path, BuiltInPackageFormat.TarGz);

        reader.TryReadTextFileAsync("package.json", CancellationToken.None).Result.ShouldBe("{\"version\":\"2.0.0\"}");
        reader.TryReadTextFileAsync("pyproject.toml", CancellationToken.None).Result.ShouldBe("py");
        reader.TryReadTextFileAsync("nope", CancellationToken.None).Result.ShouldBeNull();
        reader.ListRootFileNamesAsync(CancellationToken.None).Result.OrderBy(n => n).ShouldBe(["package.json", "pyproject.toml"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TextReader_AFileOverTheCap_IsTreatedAsMissing(bool zip)
    {
        var format = zip ? BuiltInPackageFormat.Zip : BuiltInPackageFormat.TarGz;

        var big = new string('x', PackageArchiveTextReader.MaxTextBytes + 1);
        var path = Write(format == BuiltInPackageFormat.Zip ? TestArchives.Zip(("package.json", big)) : TestArchives.TarGz(("package.json", big)));

        new PackageArchiveTextReader(path, format).TryReadTextFileAsync("package.json", CancellationToken.None).Result.ShouldBeNull();
    }
}
