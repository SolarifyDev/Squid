using System.IO;
using System.Linq;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;

namespace Squid.UnitTests.Services.Packages.BuiltIn;

[Collection("BuiltInStaging")]
public class UploadStagerTests
{
    private const long Limit = 1000;

    private static async Task<StagedUpload> StageAsync(byte[] bytes, long? declared = null)
    {
        return await UploadStager.StageAsync(new MemoryStream(bytes), Limit, declared, CancellationToken.None);
    }

    [Fact]
    public async Task Stage_WritesTheBytesToATempFile_AndHashesThem()
    {
        var bytes = new byte[500];

        new Random(1).NextBytes(bytes);

        var staged = await StageAsync(bytes);

        try
        {
            staged.SizeBytes.ShouldBe(500);
            staged.Sha256.ShouldBe(TestArchives.Sha256Of(bytes));
            (await File.ReadAllBytesAsync(staged.Path)).ShouldBe(bytes);
        }
        finally
        {
            File.Delete(staged.Path);
        }
    }

    [Fact]
    public async Task Stage_ExactlyAtTheLimit_IsAccepted()
    {
        var staged = await StageAsync(new byte[Limit]);

        File.Delete(staged.Path);

        staged.SizeBytes.ShouldBe(Limit);
    }

    [Fact]
    public async Task Stage_OneByteOverTheLimit_IsRefused()
    {
        var ex = await Should.ThrowAsync<PackageTooLargeException>(() => StageAsync(new byte[Limit + 1]));

        ex.LimitBytes.ShouldBe(Limit);
    }

    [Fact]
    public async Task Stage_OverTheLimit_AcrossManyReads_IsStillRefused()
    {
        await Should.ThrowAsync<PackageTooLargeException>(() => UploadStager.StageAsync(new SlowStream(Limit + 1), Limit, null, CancellationToken.None));
    }

    [Fact]
    public async Task Stage_AnnouncedSizeOverTheLimit_IsRefusedBeforeReading()
    {
        await Should.ThrowAsync<PackageTooLargeException>(() => StageAsync(new byte[10], declared: Limit + 1));
    }

    [Fact]
    public async Task Stage_AnnouncedSizeAtTheLimit_IsNotRefusedUpFront()
    {
        var staged = await StageAsync(new byte[10], declared: Limit);

        File.Delete(staged.Path);
    }

    [Fact]
    public async Task Stage_AnnouncedSizeThatUnderstatesTheBody_StillStopsAtTheLimit()
    {
        await Should.ThrowAsync<PackageTooLargeException>(() => StageAsync(new byte[Limit + 50], declared: 10));
    }

    [Fact]
    public async Task Stage_EmptyBody_IsRefused()
    {
        await Should.ThrowAsync<EmptyPackageUploadException>(() => StageAsync([]));
    }

    [Fact]
    public async Task Stage_Refused_LeavesNoTempFileBehind()
    {
        var before = TempFiles();

        await Should.ThrowAsync<PackageTooLargeException>(() => StageAsync(new byte[Limit + 1]));
        await Should.ThrowAsync<EmptyPackageUploadException>(() => StageAsync([]));

        TempFiles().Except(before).ShouldBeEmpty();
    }

    private static string[] TempFiles() => Directory.GetFiles(Path.GetTempPath(), "squid-builtin-push-*");

    /// <summary>Yields zeros in small reads, so a limit has to be enforced across reads and not on a single buffer.</summary>
    private sealed class SlowStream(long length) : Stream
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
            var n = (int)Math.Min(Math.Min(count, 100), length - _served);

            Array.Clear(buffer, offset, n);
            _served += n;

            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
