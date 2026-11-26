using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.Packages.BuiltIn;

/// <summary>What a push does with the version and content it is given: explicit versions are never swallowed, duplicates are found by content, and a bomb is refused quickly.</summary>
[Collection("BuiltInStaging")]
public class BuiltInPushHardeningTests
{
    private static readonly (string Name, string Content)[] Files = [("index.html", "<p>hi</p>"), ("assets/app.js", "console.log(1)"), ("assets/app.css", "p{}")];

    private readonly PushHarness _h = new();

    private static byte[] ZipAt(DateTimeOffset when, bool reverse, bool withDirectories, params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (withDirectories) zip.CreateEntry("assets/").LastWriteTime = when;

            foreach (var (name, content) in reverse ? entries.Reverse() : entries)
            {
                var entry = zip.CreateEntry(name);

                entry.LastWriteTime = when;

                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));

                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    // === explicit versions are never swallowed by the duplicate check ===

    [Fact]
    public async Task Push_ExplicitNewVersionWithTheLatestsContent_IsStoredUnderThatVersion()
    {
        var bytes = TestArchives.Zip(Files);

        await _h.Service().PushAsync(PushHarness.Push(bytes, version: "1.0.0"), CancellationToken.None);

        var second = await _h.Service().PushAsync(PushHarness.Push(bytes, version: "2.0.0"), CancellationToken.None);

        second.Deduplicated.ShouldBeFalse();
        second.Package.Version.ShouldBe("2.0.0");
        _h.Packages.Rows.Select(r => r.Version).OrderBy(v => v).ShouldBe(["1.0.0", "2.0.0"]);
    }

    [Fact]
    public async Task Push_ExplicitVersionThatIsTheLatest_WithTheSameContent_IsDeduplicated()
    {
        var bytes = TestArchives.Zip(Files);

        await _h.Service().PushAsync(PushHarness.Push(bytes, version: "1.0.0"), CancellationToken.None);

        var again = await _h.Service().PushAsync(PushHarness.Push(bytes, version: " 1.0.0 "), CancellationToken.None);

        again.Deduplicated.ShouldBeTrue();
        _h.Packages.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Push_ReplaceOfAnOlderVersionWithTheLatestsContent_ReplacesIt()
    {
        var one = TestArchives.Zip(("a.txt", "one"));
        var two = TestArchives.Zip(("a.txt", "two"));

        await _h.Service().PushAsync(PushHarness.Push(one, version: "1.0.0"), CancellationToken.None);
        await _h.Service().PushAsync(PushHarness.Push(two, version: "2.0.0"), CancellationToken.None);

        var result = await _h.Service().PushAsync(PushHarness.Push(two, version: "1.0.0", replace: true), CancellationToken.None);

        result.Deduplicated.ShouldBeFalse();
        result.Replaced.ShouldBeTrue();
        _h.Packages.Rows.Single(r => r.Version == "1.0.0").Sha256.ShouldBe(TestArchives.Sha256Of(two));
    }

    [Fact]
    public async Task Push_ReplaceNeverDeduplicates_EvenForTheLatestWithItsOwnContent()
    {
        var bytes = TestArchives.Zip(Files);

        await _h.Service().PushAsync(PushHarness.Push(bytes, version: "1.0.0"), CancellationToken.None);

        var result = await _h.Service().PushAsync(PushHarness.Push(bytes, version: "1.0.0", replace: true), CancellationToken.None);

        result.Deduplicated.ShouldBeFalse();
        result.Replaced.ShouldBeTrue();
        _h.Packages.Replaces.ShouldBe(1);
    }

    [Fact]
    public async Task Push_VersionFromTheRule_WithTheLatestsContent_IsStillDeduplicated()
    {
        var bytes = TestArchives.Zip(Files);

        await _h.Service().PushAsync(PushHarness.Push(bytes), CancellationToken.None);

        (await _h.Service().PushAsync(PushHarness.Push(bytes), CancellationToken.None)).Deduplicated.ShouldBeTrue();
    }

    [Fact]
    public async Task Push_ReplaceThatALateReleaseCheckRefuses_ChangesNothing()
    {
        await _h.Service().PushAsync(PushHarness.Push(TestArchives.Zip(("a.txt", "one")), version: "1.0.0"), CancellationToken.None);

        _h.Packages.BlockReplace = true;

        await Should.ThrowAsync<PackageVersionInUseException>(() => _h.Service().PushAsync(PushHarness.Push(TestArchives.Zip(("a.txt", "two")), version: "1.0.0", replace: true), CancellationToken.None));

        _h.Packages.Rows.Single().Sha256.ShouldBe(TestArchives.Sha256Of(TestArchives.Zip(("a.txt", "one"))));
    }

    // === duplicates are found by content, not by archive bytes ===

    [Fact]
    public async Task Push_SameFilesAsZipThenTarGz_IsADuplicate()
    {
        await _h.Service().PushAsync(PushHarness.Push(TestArchives.Zip(Files)), CancellationToken.None);

        var again = await _h.Service().PushAsync(PushHarness.Push(TestArchives.TarGz(Files), fileName: "Acme.Web.1.0.0.tar.gz", packageId: "Acme.Web"), CancellationToken.None);

        again.Deduplicated.ShouldBeTrue();
        _h.Packages.Rows.Count.ShouldBe(1);
        _h.Blobs.Blobs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Push_SameFilesWithAnotherOrderTimestampsAndDirectoryEntries_IsADuplicate()
    {
        var a = ZipAt(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), reverse: false, withDirectories: false, Files);
        var b = ZipAt(new DateTimeOffset(2024, 6, 7, 8, 9, 10, TimeSpan.Zero), reverse: true, withDirectories: true, Files);

        TestArchives.Sha256Of(a).ShouldNotBe(TestArchives.Sha256Of(b), "the archives themselves differ");

        await _h.Service().PushAsync(PushHarness.Push(a), CancellationToken.None);

        (await _h.Service().PushAsync(PushHarness.Push(b), CancellationToken.None)).Deduplicated.ShouldBeTrue();
    }

    [Fact]
    public async Task Push_TarMadeWithDotSlashNames_MatchesTheSameFilesWithoutThem()
    {
        await _h.Service().PushAsync(PushHarness.Push(TestArchives.TarGz(Files)), CancellationToken.None);

        var prefixed = TestArchives.TarGz(Files.Select(f => ("./" + f.Name, f.Content)).Append(("./", "")).ToArray());

        (await _h.Service().PushAsync(PushHarness.Push(prefixed, fileName: "Acme.Web.1.0.0.tar.gz"), CancellationToken.None)).Deduplicated.ShouldBeTrue();
    }

    [Theory]
    [InlineData("changed content")]
    [InlineData("")]
    public async Task Push_DifferentContent_IsNotADuplicate(string changedJs)
    {
        await _h.Service().PushAsync(PushHarness.Push(TestArchives.Zip(Files)), CancellationToken.None);

        var changed = TestArchives.Zip(("index.html", "<p>hi</p>"), ("assets/app.js", changedJs), ("assets/app.css", "p{}"));

        (await _h.Service().PushAsync(PushHarness.Push(changed), CancellationToken.None)).Deduplicated.ShouldBeFalse();
    }

    [Fact]
    public async Task Push_SameContentUnderADifferentPath_IsNotADuplicate()
    {
        await _h.Service().PushAsync(PushHarness.Push(TestArchives.Zip(("a/x.txt", "same"))), CancellationToken.None);

        (await _h.Service().PushAsync(PushHarness.Push(TestArchives.Zip(("b/x.txt", "same"))), CancellationToken.None)).Deduplicated.ShouldBeFalse();
    }

    [Fact]
    public async Task Push_ARowWithoutAFingerprint_NeverDeduplicates_EvenWithTheSameArchiveBytes()
    {
        var bytes = TestArchives.Zip(Files);

        _h.Packages.Seed(_h.Feed.Id, "Acme.Web", "0.9.0", TestArchives.Sha256Of(bytes), fingerprint: null);

        var result = await _h.Service().PushAsync(PushHarness.Push(bytes, packageId: "Acme.Web"), CancellationToken.None);

        result.Deduplicated.ShouldBeFalse();
    }

    [Fact]
    public async Task Push_RecordsBothTheArchiveHash_AndTheContentFingerprint()
    {
        var bytes = TestArchives.Zip(Files);

        await _h.Service().PushAsync(PushHarness.Push(bytes), CancellationToken.None);

        var row = _h.Packages.Rows.Single();

        row.Sha256.ShouldBe(TestArchives.Sha256Of(bytes));
        row.ContentFingerprint.ShouldNotBeNullOrEmpty();
        row.ContentFingerprint.Length.ShouldBe(64);
        row.ContentFingerprint.ShouldNotBe(row.Sha256);
    }

    // === the walk: limits, one pass ===

    private static ArchiveLimits Small => new(MaxEntries: 3, MaxUncompressedBytes: 1000, MaxCompressionRatio: 5, CompressionRatioFloorBytes: 100);

    private static string Write(byte[] bytes)
    {
        var path = Path.GetTempFileName();

        File.WriteAllBytes(path, bytes);

        return path;
    }

    private static T WithFile<T>(byte[] bytes, Func<string, T> use)
    {
        var path = Write(bytes);

        try { return use(path); }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Inspect_TooManyEntries_IsRefused(bool zip)
    {
        var entries = Enumerable.Range(0, 4).Select(i => ($"f{i}.txt", "x")).ToArray();
        var bytes = zip ? TestArchives.Zip(entries) : TestArchives.TarGz(entries);

        Should.Throw<PackageArchiveLimitExceededException>(() => WithFile(bytes, p => PackageArchiveInspector.Inspect(p, zip ? BuiltInPackageFormat.Zip : BuiltInPackageFormat.TarGz, "p", Small))).Message.ShouldContain("more than 3 entries");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Inspect_TooManyUnpackedBytes_IsRefused(bool zip)
    {
        var content = new string('x', 1200);
        var bytes = zip ? TestArchives.Zip(("a.txt", content)) : TestArchives.TarGz(("a.txt", content));
        var limits = Small with { MaxCompressionRatio = 100000 };

        Should.Throw<PackageArchiveLimitExceededException>(() => WithFile(bytes, p => PackageArchiveInspector.Inspect(p, zip ? BuiltInPackageFormat.Zip : BuiltInPackageFormat.TarGz, "p", limits))).Message.ShouldContain("more than 1000 bytes");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Inspect_AnArchiveThatUnpacksFarBeyondItsSize_IsRefusedByRatio_OnlyAfterTheFloor(bool zip)
    {
        var format = zip ? BuiltInPackageFormat.Zip : BuiltInPackageFormat.TarGz;
        var limits = new ArchiveLimits(10, 100_000_000, 5, 100);
        var bomb = zip ? TestArchives.Zip(("a.txt", new string('0', 50_000))) : TestArchives.TarGz(("a.txt", new string('0', 50_000)));
        var small = zip ? TestArchives.Zip(("a.txt", new string('0', 60))) : TestArchives.TarGz(("a.txt", new string('0', 60)));

        Should.Throw<PackageArchiveLimitExceededException>(() => WithFile(bomb, p => PackageArchiveInspector.Inspect(p, format, "p", limits))).Message.ShouldContain("5 times");
        Should.NotThrow(() => WithFile(small, p => PackageArchiveInspector.Inspect(p, format, "p", limits)));
    }

    [Fact]
    public void Limits_Defaults_AreTheNamedConstants()
    {
        var limits = ArchiveLimits.Default;

        limits.MaxEntries.ShouldBe(BuiltInFeed.MaxArchiveEntries);
        limits.MaxUncompressedBytes.ShouldBe(BuiltInFeed.MaxUncompressedBytes);
        limits.MaxCompressionRatio.ShouldBe(BuiltInFeed.MaxCompressionRatio);
        limits.CompressionRatioFloorBytes.ShouldBe(BuiltInFeed.CompressionRatioFloorBytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Push_ABombUnderTheDefaultLimits_IsRefusedQuickly_AndNothingIsStored(bool zip)
    {
        var bomb = BuildBomb(zip);
        var watch = Stopwatch.StartNew();

        await Should.ThrowAsync<PackageArchiveLimitExceededException>(() => _h.Service().PushAsync(PushHarness.Push(bomb, fileName: zip ? "Acme.Web.1.0.0.zip" : "Acme.Web.1.0.0.tar.gz"), CancellationToken.None));

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        _h.Blobs.Blobs.ShouldBeEmpty();
        _h.Packages.Rows.ShouldBeEmpty();
        _h.Packages.CanonicalCalls.ShouldBe(0, "a refused archive never fixes a package's casing");
    }

    // 2 GiB of zeros would pass the floor many times over; the walk has to stop at the first 256 MiB past it, so the test only needs a bit more than the floor.
    private static byte[] BuildBomb(bool zip)
    {
        var length = BuiltInFeed.CompressionRatioFloorBytes + 64L * 1024 * 1024;

        using var buffer = new MemoryStream();

        if (zip)
        {
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = archive.CreateEntry("zeros.bin", CompressionLevel.SmallestSize).Open())
                WriteZeros(entry, length);
        }
        else
        {
            // A tar entry of unknown-to-the-writer length can only be written into a seekable archive, so the tar goes to a temp file and is gzipped from there.
            var tarPath = Path.GetTempFileName();

            try
            {
                using (var tarFile = new FileStream(tarPath, FileMode.Create))
                using (var tar = new TarWriter(tarFile, TarEntryFormat.Pax, leaveOpen: false))
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "zeros.bin") { DataStream = new ZeroStream(length) });

                using var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true);
                using var tarRead = File.OpenRead(tarPath);

                tarRead.CopyTo(gzip);
            }
            finally
            {
                File.Delete(tarPath);
            }
        }

        return buffer.ToArray();
    }

    private static void WriteZeros(Stream to, long length)
    {
        var chunk = new byte[1024 * 1024];

        for (long written = 0; written < length; written += chunk.Length)
            to.Write(chunk, 0, (int)Math.Min(chunk.Length, length - written));
    }

    [Fact]
    public void Inspect_GivesTheRootListingAndManifests_AndTheTextReaderNeedsNoSecondRead()
    {
        var bytes = TestArchives.TarGz(("./package.json", "{\"version\":\"1.2.3\"}"), ("./sub/other.txt", "x"), ("./app.csproj", "<Project/>"));
        var path = Write(bytes);

        var inspection = PackageArchiveInspector.Inspect(path, BuiltInPackageFormat.TarGz, "p", ArchiveLimits.Default);

        File.Delete(path);

        var reader = new PackageArchiveTextReader(path, BuiltInPackageFormat.TarGz, inspection);

        inspection.RootFileNames.OrderBy(n => n).ShouldBe(["app.csproj", "package.json"]);
        reader.ListRootFileNamesAsync(CancellationToken.None).Result.OrderBy(n => n).ShouldBe(["app.csproj", "package.json"]);
        reader.TryReadTextFileAsync("package.json", CancellationToken.None).Result.ShouldBe("{\"version\":\"1.2.3\"}");
        reader.TryReadTextFileAsync("pyproject.toml", CancellationToken.None).Result.ShouldBeNull("the walk saw every root manifest, so a missing one is missing");
    }

    [Fact]
    public void Inspect_UnsafeEntry_StaysRefused_ByTheSharedPredicate()
    {
        var ex = Should.Throw<PackageArchiveRejectedException>(() => WithFile(TestArchives.Zip(("ok.txt", "x"), ("a/../../evil", "y")), p => PackageArchiveInspector.Inspect(p, BuiltInPackageFormat.Zip, "p", ArchiveLimits.Default)));

        ex.Message.ShouldContain("escape");
    }

    // === the staged file is removed without ever masking the push's own outcome ===

    [Fact]
    public void DeleteStagedFile_ThatCannotBeRemoved_DoesNotThrow()
    {
        var service = _h.Service();
        var contextType = typeof(BuiltInPackagePushService).GetNestedType("PushContext", System.Reflection.BindingFlags.NonPublic);
        var context = Activator.CreateInstance(contextType, PushHarness.Push([1]));
        var directory = Directory.CreateTempSubdirectory();

        try
        {
            contextType.GetProperty("Staged").SetValue(context, new StagedUpload(directory.FullName, 1, "x"));
            typeof(BuiltInPackagePushService).GetField("_ctx", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(service, context);

            Should.NotThrow(() => typeof(BuiltInPackagePushService).GetMethod("DeleteStagedFile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(service, null));
        }
        finally
        {
            directory.Delete();
        }
    }

    private sealed class ZeroStream(long length) : Stream
    {
        private long _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _served; set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _served);

            Array.Clear(buffer, offset, n);
            _served += n;

            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
