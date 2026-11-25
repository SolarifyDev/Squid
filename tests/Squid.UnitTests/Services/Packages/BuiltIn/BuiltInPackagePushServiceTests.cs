using System.IO;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Message.Commands.BuiltInPackages;
using Squid.Message.Constants;
using Squid.Message.Enums.BuiltInPackages;

namespace Squid.UnitTests.Services.Packages.BuiltIn;

/// <summary>The push pipeline end to end over in-memory fakes of its edges (index, store, releases) and the real version assigner and archive code.</summary>
[Collection("BuiltInStaging")]
public class BuiltInPackagePushServiceTests
{
    private static readonly byte[] WebV1 = TestArchives.Zip(("index.html", "one"));
    private static readonly byte[] WebV2 = TestArchives.Zip(("index.html", "two"));

    private readonly PushHarness _h = new();

    private static string Sha(byte[] bytes) => TestArchives.Sha256Of(bytes);

    [Fact]
    public async Task Push_NewPackage_StoresBlobAtContentKey_AndIndexesIt()
    {
        var result = await _h.Service().PushAsync(PushHarness.Push(WebV1, version: "1.2.3"), CancellationToken.None);

        var key = $"sha256/{Sha(WebV1)[..2]}/{Sha(WebV1)}.zip";

        result.Deduplicated.ShouldBeFalse();
        result.Replaced.ShouldBeFalse();
        result.Package.PackageId.ShouldBe("Acme.Web");
        result.Package.Version.ShouldBe("1.2.3");
        result.Package.Sha256.ShouldBe(Sha(WebV1));
        result.Package.SizeBytes.ShouldBe(WebV1.Length);
        result.Package.FileExtension.ShouldBe(".zip");
        _h.Blobs.Blobs.Keys.ShouldBe([key]);
        _h.Blobs.Blobs[key].ShouldBe(WebV1);
        _h.Packages.Rows.Single().BlobKey.ShouldBe(key);
    }

    [Fact]
    public async Task Push_TarGz_KeepsItsRealExtension_EvenWhenTheFileIsNamedZip()
    {
        var tar = TestArchives.TarGz(("app/run.sh", "echo"));

        var result = await _h.Service().PushAsync(PushHarness.Push(tar, fileName: "Acme.Web.1.0.0.zip", version: "1.0.0"), CancellationToken.None);

        result.Package.FileExtension.ShouldBe(".tar.gz");
        _h.Blobs.Blobs.Keys.Single().ShouldEndWith(".tar.gz");
    }

    [Fact]
    public async Task Push_SameBytesAsLatest_ReturnsLatest_WithoutAssigningAVersionOrStoringAnything()
    {
        await _h.Service().PushAsync(PushHarness.Push(WebV1), CancellationToken.None);

        var assignsBefore = _h.Assigner.Assigns.Count;
        var counterBefore = _h.Counter.Calls.Count;
        var putsBefore = _h.Blobs.Puts;

        var again = await _h.Service().PushAsync(PushHarness.Push(WebV1, fileName: "other-name.zip", packageId: "Acme.Web"), CancellationToken.None);

        again.Deduplicated.ShouldBeTrue();
        again.Package.Version.ShouldBe("1.0.1");
        _h.Assigner.Assigns.Count.ShouldBe(assignsBefore, "a deduplicated push must not even ask the assigner");
        _h.Counter.Calls.Count.ShouldBe(counterBefore, "a deduplicated push must not consume a counter number");
        _h.Blobs.Puts.ShouldBe(putsBefore);
        _h.Packages.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Push_SameBytesAsAnOlderVersionButNotTheLatest_IsANewVersion()
    {
        await _h.Service().PushAsync(PushHarness.Push(WebV1), CancellationToken.None);
        await _h.Service().PushAsync(PushHarness.Push(WebV2), CancellationToken.None);

        var third = await _h.Service().PushAsync(PushHarness.Push(WebV1), CancellationToken.None);

        third.Deduplicated.ShouldBeFalse();
        third.Package.Version.ShouldBe("1.0.3");
        _h.Blobs.Blobs.Count.ShouldBe(2, "the bytes of the first version are already in the store, so they are kept once");
    }

    [Fact]
    public async Task Push_ExistingBlob_IsNotWrittenAgain()
    {
        var existingKey = BuiltInBlobKey.For(Sha(WebV1), ".zip");

        _h.Blobs.Blobs[existingKey] = WebV1;

        await _h.Service().PushAsync(PushHarness.Push(WebV1), CancellationToken.None);

        _h.Blobs.Puts.ShouldBe(0);
        _h.Packages.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Push_VersionAssignedFromTheRule_UsesTheFeedPackageTemplate()
    {
        _h.Rules = new FakeRules("2.{counter}");

        var first = await _h.Service().PushAsync(PushHarness.Push(WebV1), CancellationToken.None);
        var second = await _h.Service().PushAsync(PushHarness.Push(WebV2), CancellationToken.None);

        first.Package.Version.ShouldBe("2.1");
        second.Package.Version.ShouldBe("2.2");
    }

    [Fact]
    public async Task Push_ExplicitVersion_GoesThroughTheAssignerAsAnInputTemplate()
    {
        await _h.Service().PushAsync(PushHarness.Push(WebV1, version: "4.5.6"), CancellationToken.None);

        var (template, context) = _h.Assigner.Assigns.Single();

        template.ShouldBe("{input}");
        context.InputVersion.ShouldBe("4.5.6");
        _h.Counter.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Push_ExplicitVersionThatCannotSort_IsRefusedAndNothingIsStored()
    {
        await Should.ThrowAsync<PackageVersionTemplateException>(() => _h.Service().PushAsync(PushHarness.Push(WebV1, version: "banana"), CancellationToken.None));

        _h.Blobs.Blobs.ShouldBeEmpty();
        _h.Packages.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Push_SourceVersionTemplate_ReadsTheManifestFromTheArchive()
    {
        _h.Rules = new FakeRules("{source.version}");

        var withManifest = TestArchives.TarGz(("package.json", "{\"name\":\"web\",\"version\":\"3.4.5\"}"));

        var result = await _h.Service().PushAsync(PushHarness.Push(withManifest), CancellationToken.None);

        result.Package.Version.ShouldBe("3.4.5");
    }

    [Fact]
    public async Task Push_FileVersionTemplate_ReadsTheFileName()
    {
        _h.Rules = new FakeRules("{file.version}");

        var result = await _h.Service().PushAsync(PushHarness.Push(WebV1, fileName: "Acme.Web.7.8.9-rc.1.zip"), CancellationToken.None);

        result.Package.Version.ShouldBe("7.8.9-rc.1");
    }

    [Fact]
    public async Task Push_GitShaTemplate_ReadsTheCommandsGitSha()
    {
        _h.Rules = new FakeRules("1.0.0-{git.sha:7}");

        var command = PushHarness.Push(WebV1);

        command.GitSha = "0123456789abcdef0123456789abcdef01234567";

        var result = await _h.Service().PushAsync(command, CancellationToken.None);

        result.Package.Version.ShouldBe("1.0.0-0123456");
    }

    [Fact]
    public async Task Push_OtherCasing_ReusesTheCanonicalIdForTheIndexTheRuleAndTheCounter()
    {
        await _h.Service().PushAsync(PushHarness.Push(WebV1, packageId: "Acme.Web"), CancellationToken.None);

        var second = await _h.Service().PushAsync(PushHarness.Push(WebV2, packageId: "ACME.WEB"), CancellationToken.None);

        second.Package.PackageId.ShouldBe("Acme.Web");
        second.Package.Version.ShouldBe("1.0.2");
        _h.Packages.Rows.Select(r => r.PackageId).Distinct().ShouldBe(["Acme.Web"]);
        _h.Rules.AskedFor.ShouldBe(["Acme.Web", "Acme.Web"]);
        _h.Counter.Calls.Select(c => c.PackageId).Distinct().ShouldBe(["Acme.Web"]);
    }

    [Fact]
    public async Task Push_PackageIdFromTheFileName_WhenNoneIsGiven()
    {
        var result = await _h.Service().PushAsync(PushHarness.Push(WebV1, fileName: "Contoso.Api.2.0.0.zip"), CancellationToken.None);

        result.Package.PackageId.ShouldBe("Contoso.Api");
    }

    [Fact]
    public async Task Push_NoPackageIdAnywhere_AsksForOne_BeforeReadingTheBody()
    {
        var command = PushHarness.Push(WebV1, fileName: "upload.zip");
        var body = new ThrowingStream();

        command.Content = body;

        await Should.ThrowAsync<PackageIdRequiredException>(() => _h.Service().PushAsync(command, CancellationToken.None));

        body.WasRead.ShouldBeFalse();
    }

    [Theory]
    [InlineData("plain text, not an archive")]
    [InlineData("PK is not enough")]
    public async Task Push_NotAnArchive_IsRefusedByContent_NotByName(string text)
    {
        var command = PushHarness.Push(System.Text.Encoding.UTF8.GetBytes(text), fileName: "Acme.Web.1.0.0.zip");

        await Should.ThrowAsync<UnsupportedPackageFormatException>(() => _h.Service().PushAsync(command, CancellationToken.None));

        _h.Blobs.Blobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Push_PlainTar_IsRefused()
    {
        await Should.ThrowAsync<UnsupportedPackageFormatException>(() => _h.Service().PushAsync(PushHarness.Push(TestArchives.PlainTar(("a.txt", "x"))), CancellationToken.None));
    }

    [Fact]
    public async Task Push_EmptyBody_IsRefused()
    {
        await Should.ThrowAsync<EmptyPackageUploadException>(() => _h.Service().PushAsync(PushHarness.Push([]), CancellationToken.None));
    }

    [Fact]
    public async Task Push_ZipWithATraversalEntry_IsRefusedByTheSharedArchiveSafetyCheck()
    {
        var evil = TestArchives.Zip(("../../etc/cron.d/x", "boom"));

        var ex = await Should.ThrowAsync<PackageArchiveRejectedException>(() => _h.Service().PushAsync(PushHarness.Push(evil), CancellationToken.None));

        ex.Message.ShouldContain("escape");
        _h.Blobs.Blobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Push_GzipThatIsNotATar_IsRefused()
    {
        using var buffer = new MemoryStream();

        using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(System.Text.Encoding.UTF8.GetBytes("just text, not a tar archive at all, padded to be longer than one tar block................................................................................................................................................................................................................................................................................................................"));

        await Should.ThrowAsync<PackageArchiveRejectedException>(() => _h.Service().PushAsync(PushHarness.Push(buffer.ToArray()), CancellationToken.None));
    }

    [Fact]
    public async Task Push_RefusedBeforeTheChecksPass_NeverFixesThePackageCasing()
    {
        await Should.ThrowAsync<UnsupportedPackageFormatException>(() => _h.Service().PushAsync(PushHarness.Push([1, 2, 3, 4, 5]), CancellationToken.None));

        _h.Packages.CanonicalCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Push_AnnouncedSizeOverTheLimit_IsRefusedWithoutReadingTheBody()
    {
        var command = PushHarness.Push(WebV1);
        var body = new ThrowingStream();

        command.Content = body;
        command.DeclaredSizeBytes = BuiltInFeed.MaxPackageSizeBytes + 1;

        await Should.ThrowAsync<PackageTooLargeException>(() => _h.Service().PushAsync(command, CancellationToken.None));

        body.WasRead.ShouldBeFalse();
    }

    [Fact]
    public async Task Push_ExistingVersion_WithoutReplace_IsRefused()
    {
        await _h.Service().PushAsync(PushHarness.Push(WebV1, version: "1.0.0"), CancellationToken.None);

        await Should.ThrowAsync<PackageVersionAlreadyExistsException>(() => _h.Service().PushAsync(PushHarness.Push(WebV2, version: "1.0.0"), CancellationToken.None));

        _h.Packages.Rows.Single().Sha256.ShouldBe(Sha(WebV1));
    }

    [Fact]
    public async Task Push_ReplaceOfAVersionAReleaseSelects_IsRefused()
    {
        await _h.Service().PushAsync(PushHarness.Push(WebV1, version: "1.0.0"), CancellationToken.None);

        _h.Usage.Selected.Add(("acme.web", "1.0.0"));

        await Should.ThrowAsync<PackageVersionInUseException>(() => _h.Service().PushAsync(PushHarness.Push(WebV2, version: "1.0.0", replace: true), CancellationToken.None));

        _h.Packages.Rows.Single().Sha256.ShouldBe(Sha(WebV1));
        _h.Packages.Replaces.ShouldBe(0);
    }

    [Fact]
    public async Task Push_ReplaceOfAVersionNoReleaseSelects_OverwritesIt()
    {
        var first = await _h.Service().PushAsync(PushHarness.Push(WebV1, version: "1.0.0"), CancellationToken.None);

        _h.Usage.Selected.Add(("Acme.Web", "9.9.9"));

        var result = await _h.Service().PushAsync(PushHarness.Push(WebV2, version: "1.0.0", replace: true), CancellationToken.None);

        result.Replaced.ShouldBeTrue();
        result.Package.Id.ShouldBe(first.Package.Id);
        _h.Packages.Rows.Single().Sha256.ShouldBe(Sha(WebV2));
        _h.Packages.Replaces.ShouldBe(1);
    }

    [Fact]
    public async Task Push_ReplaceWithTheLatestsOwnBytes_IsADeduplicationNotAWrite()
    {
        await _h.Service().PushAsync(PushHarness.Push(WebV1, version: "1.0.0"), CancellationToken.None);

        var result = await _h.Service().PushAsync(PushHarness.Push(WebV1, version: "1.0.0", replace: true), CancellationToken.None);

        result.Deduplicated.ShouldBeTrue();
        result.Replaced.ShouldBeFalse();
        _h.Packages.Replaces.ShouldBe(0);
    }

    [Fact]
    public async Task Push_NoStoreIdOnTheFeed_UsesAndRecordsTheDefaultStore()
    {
        var result = await _h.Service().PushAsync(PushHarness.Push(WebV1), CancellationToken.None);

        _h.Registry.Resolved.ShouldBe([1]);
        _h.Packages.Rows.Single().StoreId.ShouldBe(1);
        result.Package.Id.ShouldBe(_h.Packages.Rows.Single().Id);
    }

    [Fact]
    public async Task Push_FeedNamesAStore_UsesThatStore()
    {
        _h.Feed.Properties = "{\"StoreId\":\"42\"}";

        await _h.Service().PushAsync(PushHarness.Push(WebV1), CancellationToken.None);

        _h.Registry.Resolved.ShouldBe([42]);
        _h.Packages.Rows.Single().StoreId.ShouldBe(42);
    }

    [Fact]
    public async Task Push_FeedStoreIdIsNotANumber_IsRefused()
    {
        _h.Feed.Properties = "{\"StoreId\":\"local\"}";

        await Should.ThrowAsync<BuiltInFeedStoreInvalidException>(() => _h.Service().PushAsync(PushHarness.Push(WebV1), CancellationToken.None));

        _h.Packages.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Push_NoDefaultStoreAndNoneNamed_IsRefused()
    {
        var harness = new PushHarness();

        harness.Stores.Rows.ToList().ForEach(r => r.IsDefault = false);

        await Should.ThrowAsync<NoDefaultPackageStoreException>(() => harness.Service().PushAsync(PushHarness.Push(WebV1), CancellationToken.None));
    }

    [Fact]
    public async Task Push_RecordsTheSourceInLowerCase()
    {
        var command = PushHarness.Push(WebV1);

        command.Source = BuiltInPackageSource.Cli;

        var result = await _h.Service().PushAsync(command, CancellationToken.None);

        result.Package.Source.ShouldBe("cli");
    }

    [Fact]
    public async Task Push_ToASpaceWithoutABuiltInFeed_IsRefused()
    {
        var command = PushHarness.Push(WebV1);

        command.SpaceId = 99;

        await Should.ThrowAsync<BuiltInFeedNotFoundException>(() => _h.Service().PushAsync(command, CancellationToken.None));
    }

    /// <summary>A body that fails the test if anything reads it.</summary>
    private sealed class ThrowingStream : Stream
    {
        public bool WasRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { WasRead = true; throw new InvalidOperationException("the body must not be read"); }
        public override System.Threading.Tasks.ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { WasRead = true; throw new InvalidOperationException("the body must not be read"); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
