using System.Text;
using Halibut;
using Shouldly;
using Squid.Tentacle.FileTransfer;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.FileTransfer;

/// <summary>
/// P1-Phase9b.3 — LocalFileTransferService unit tests.
///
/// <para>Pin the workspace-boundary contract: rooted / traversal paths get
/// rewritten to a hash-derived filename inside the upload root. Defence-in-
/// depth against a server that's been compromised or talks to an unsanctioned
/// agent (e.g. a man-in-the-middle replaying upload commands with
/// <c>/etc/cron.d/abc</c> as the target path).</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class LocalFileTransferServiceTests : IDisposable
{
    private readonly string _uploadRoot;

    public LocalFileTransferServiceTests()
    {
        _uploadRoot = Path.Combine(Path.GetTempPath(), "squid-filetransfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_uploadRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_uploadRoot, recursive: true); } catch { /* best-effort */ }
    }

    // ── Workspace-boundary path rewriting ────────────────────────────────────

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/etc/cron.d/abc")]
    [InlineData("/root/.ssh/authorized_keys")]
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts")]
    public void ResolveSafePath_RootedPath_RewritesToHashedFilenameInRoot(string maliciousPath)
    {
        var svc = new LocalFileTransferService(_uploadRoot);

        var resolved = svc.ResolveSafePath(maliciousPath);

        resolved.ShouldStartWith(_uploadRoot, customMessage:
            $"Rooted path '{maliciousPath}' must NOT escape the upload root.");
        Path.GetFileName(resolved).ShouldStartWith("rewritten-",
            customMessage: "Rooted paths must be rewritten with the 'rewritten-' prefix marker.");
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("../sibling")]
    [InlineData("a/../../escaped")]
    public void ResolveSafePath_TraversalPath_RewritesToHashedFilenameInRoot(string traversalPath)
    {
        var svc = new LocalFileTransferService(_uploadRoot);

        var resolved = svc.ResolveSafePath(traversalPath);

        resolved.ShouldStartWith(_uploadRoot);
        Path.GetFileName(resolved).ShouldStartWith("rewritten-");
    }

    [Theory]
    [InlineData("hello.txt")]
    [InlineData("subfolder/nested.json")]
    [InlineData("a/b/c/d.bin")]
    public void ResolveSafePath_RelativePath_KeepsStructureUnderRoot(string safePath)
    {
        var svc = new LocalFileTransferService(_uploadRoot);

        var resolved = svc.ResolveSafePath(safePath);

        resolved.ShouldStartWith(_uploadRoot);
        // Convert path to forward-slash for portable assertion
        resolved.Replace('\\', '/').ShouldEndWith(safePath);
    }

    [Fact]
    public void ResolveSafePath_EmptyOrWhitespace_Throws()
    {
        var svc = new LocalFileTransferService(_uploadRoot);
        Should.Throw<ArgumentException>(() => svc.ResolveSafePath(""));
        Should.Throw<ArgumentException>(() => svc.ResolveSafePath(null));
        Should.Throw<ArgumentException>(() => svc.ResolveSafePath("   "));
    }

    // ── Round-trip upload + download ────────────────────────────────────────

    [Fact]
    public void Upload_ThenDownload_RoundTripsBytes()
    {
        var svc = new LocalFileTransferService(_uploadRoot);
        var payload = Encoding.UTF8.GetBytes("phase-9b.3 round-trip payload — line one\nline two\n");

        var result = svc.UploadFile("hello.txt", DataStream.FromBytes(payload));

        result.ShouldNotBeNull();
        result.Length.ShouldBe(payload.Length);
        result.Hash.Length.ShouldBe(64, customMessage:
            "SHA-256 hex digest must be 64 lowercase hex chars.");
        result.FullPath.ShouldStartWith(_uploadRoot);

        // Download via the SAME service — operator-visible path round-trip
        var downloaded = svc.DownloadFile("hello.txt");
        var downloadedBytes = ReadAllBytes(downloaded);

        downloadedBytes.ShouldBe(payload);
    }

    [Fact]
    public void Upload_HashStableAcrossRuns()
    {
        // Same payload uploaded twice → same SHA-256. Lets server verify
        // integrity by hash compare.
        var svc1 = new LocalFileTransferService(_uploadRoot);
        var svc2 = new LocalFileTransferService(_uploadRoot);
        var payload = Encoding.UTF8.GetBytes("integrity-check");

        var r1 = svc1.UploadFile("h.txt", DataStream.FromBytes(payload));
        var r2 = svc2.UploadFile("h.txt", DataStream.FromBytes(payload));

        r1.Hash.ShouldBe(r2.Hash);
    }

    [Fact]
    public void Download_NonExistentFile_ThrowsFileNotFound()
    {
        var svc = new LocalFileTransferService(_uploadRoot);
        Should.Throw<FileNotFoundException>(() => svc.DownloadFile("nonexistent.txt"));
    }

    [Fact]
    public void Constructor_NullUploadRoot_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new LocalFileTransferService(null));
    }

    [Fact]
    public void Upload_NullDataStream_Throws()
    {
        var svc = new LocalFileTransferService(_uploadRoot);
        Should.Throw<ArgumentNullException>(() => svc.UploadFile("x.txt", null));
    }

    private static byte[] ReadAllBytes(DataStream ds)
    {
        using var ms = new MemoryStream();
        ds.Receiver()
          .ReadAsync(async (s, ct) => await s.CopyToAsync(ms, ct), CancellationToken.None)
          .GetAwaiter().GetResult();
        return ms.ToArray();
    }
}
