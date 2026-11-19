using System.IO;
using Squid.Core.Services.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public sealed class OctopusImportAtomicFileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("squid-atomic-file-").FullName;

    [Fact]
    public async Task Write_ReplacesTheFile_LeavesNothingElse()
    {
        var path = Path.Combine(_directory, "doc.json");
        await File.WriteAllTextAsync(path, "old");

        await OctopusImportAtomicFile.WriteAsync(path, (stream, ct) => stream.WriteAsync("new"u8.ToArray(), ct).AsTask(), CancellationToken.None);

        (await File.ReadAllTextAsync(path)).ShouldBe("new");
        Directory.GetFiles(_directory).ShouldBe([path]);
    }

    // A disk that fills part way through: what was written so far must never replace the complete file already there.
    [Fact]
    public async Task WriteFailingPartWay_PreviousFileUntouched_NoPartialLeft()
    {
        var path = Path.Combine(_directory, "doc.json");
        await File.WriteAllTextAsync(path, "complete");

        async Task FillThenFail(Stream stream, CancellationToken ct)
        {
            await stream.WriteAsync("trunc"u8.ToArray(), ct);
            await stream.FlushAsync(ct);
            throw new IOException("No space left on device");
        }

        await Should.ThrowAsync<IOException>(() => OctopusImportAtomicFile.WriteAsync(path, FillThenFail, CancellationToken.None));

        (await File.ReadAllTextAsync(path)).ShouldBe("complete");
        Directory.GetFiles(_directory).ShouldBe([path]);
    }

    // A scan for staged documents ("*.json") must never pick up a write still in progress.
    [Fact]
    public async Task WriteInProgress_NotMatchedByTheTargetExtension()
    {
        var path = Path.Combine(_directory, "doc.json");
        string[] seen = null;

        await OctopusImportAtomicFile.WriteAsync(path, (_, _) =>
        {
            seen = Directory.GetFiles(_directory, "*.json");
            return Task.CompletedTask;
        }, CancellationToken.None);

        seen.ShouldBeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
