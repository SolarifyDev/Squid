using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Squid.WindowsUpgradeE2ETests.Infrastructure;

/// <summary>
/// In-process HTTP server stub that mimics the GitHub Releases / private-mirror
/// download surface the production <c>install-tentacle.{sh,ps1}</c> scripts
/// expect. Used by Phase 12.K E2E tests to drive the install scripts WITHOUT
/// touching the real GitHub Releases CDN.
///
/// <para><b>Wire shapes mirrored from the production scripts</b>:</para>
/// <list type="bullet">
///   <item><c>GET /latest/download/squid-tentacle-{rid}.zip</c> → 200 + zip body
///         (the <c>--version latest</c> path, used by both ps1 and sh)</item>
///   <item><c>GET /download/{version}/squid-tentacle-{version}-{rid}.zip</c>
///         → 200 + zip body (Windows pinned-version path)</item>
///   <item><c>GET /download/v{version}/squid-tentacle-{version}-{rid}.zip</c>
///         → fallback URL the scripts try if the un-prefixed tag 404s</item>
///   <item><c>GET /download/{version}/squid-tentacle-{version}-{rid}.tar.gz</c>
///         → Linux tarball variant</item>
/// </list>
///
/// <para><b>Tier</b>: 🔵 Fixture-only (Rule 12) — test infrastructure for
/// the install-script E2E suite. Tests using it achieve 🟢 high-fidelity
/// because the install scripts themselves are production code; only the
/// upstream release CDN is replaced.</para>
///
/// <para><b>Cross-platform</b>: <see cref="HttpListener"/> works identically
/// on Windows / Linux / macOS for plain HTTP loopback bindings. Tests don't
/// need OS-specific branches at the fixture level.</para>
///
/// <para><b>Configurable per-test</b>:</para>
/// <list type="bullet">
///   <item><see cref="StageBinary"/> — supplies the byte content packed into
///         the served zip / tarball. Tests use a tiny fake binary content
///         (e.g. an empty file or a dummy script) — install scripts only
///         verify the binary EXISTS post-extract, not that it actually runs.</item>
///   <item><see cref="ConfigureNotFoundForVersion"/> — makes the mirror
///         return 404 for a specific version, exercising the script's
///         fallback / error path.</item>
/// </list>
///
/// <para><b>Lifetime</b>: <see cref="IDisposable"/>. Each test creates its
/// own instance via <see cref="Start"/>; unique loopback port per fixture
/// (Rule 12.8) means concurrent tests don't collide.</para>
/// </summary>
public sealed class LocalReleaseMirror : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _loopCts;
    private readonly Task _loopTask;
    private byte[] _stagedBinary;
    private string _stagedBinaryFileName;
    private readonly HashSet<string> _notFoundVersions = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>The mirror's base URL. Pass this as <c>--DownloadBase</c>
    /// (Windows) / <c>DOWNLOAD_BASE</c> env (Linux) to the install script.</summary>
    public Uri BaseUri { get; }

    /// <summary>The bound loopback port (Rule 12.8 — OS-allocated).</summary>
    public int Port { get; }

    /// <summary>
    /// Snapshot of every download path requested. Tests assert on this to
    /// prove the script actually hit the mirror (vs falling through to the
    /// real GitHub URL).
    /// </summary>
    public IReadOnlyList<string> ReceivedRequests => _receivedRequests;

    private readonly List<string> _receivedRequests = new();
    private readonly object _requestsLock = new();

    private LocalReleaseMirror(HttpListener listener, int port, CancellationTokenSource loopCts)
    {
        _listener = listener;
        _loopCts = loopCts;

        Port = port;
        BaseUri = new Uri($"http://localhost:{port}/");

        _loopTask = Task.Run(() => RunLoopAsync(loopCts.Token));
    }

    /// <summary>Starts the mirror on a unique loopback port.</summary>
    public static LocalReleaseMirror Start()
    {
        var port = GetEphemeralPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var cts = new CancellationTokenSource();
        return new LocalReleaseMirror(listener, port, cts);
    }

    /// <summary>
    /// Stages the binary content that will be packed into every zip /
    /// tarball the mirror serves. Default is a tiny shim file. Tests can
    /// override with PowerShell scripts / shell scripts so the install
    /// script's post-extract probes (e.g. <c>squid-tentacle help</c>)
    /// produce expected output.
    /// </summary>
    public void StageBinary(string binaryFileName, byte[] content)
    {
        _stagedBinaryFileName = binaryFileName;
        _stagedBinary = content;
    }

    /// <summary>
    /// Configures the mirror to return 404 for the given version (any
    /// download URL containing the version). Used by tests that exercise
    /// the script's bogus-version error path.
    /// </summary>
    public void ConfigureNotFoundForVersion(string version)
    {
        _notFoundVersions.Add(version);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _loopCts.Cancel(); } catch { }
        try { _listener.Stop(); _listener.Close(); } catch { }
        try { _loopTask.Wait(2_000); } catch { }
    }

    // ── HTTP loop ───────────────────────────────────────────────────────────

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; }

            try { HandleRequest(ctx); }
            catch
            {
                try
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.OutputStream.Close();
                }
                catch { }
            }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? string.Empty;

        lock (_requestsLock)
        {
            _receivedRequests.Add(path);
        }

        // 404 if the path mentions any of the configured "not found" versions.
        if (_notFoundVersions.Any(v => path.Contains(v, StringComparison.OrdinalIgnoreCase)))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.OutputStream.Close();
            return;
        }

        // Serve a zip / tarball depending on the URL extension. The
        // install-tentacle.{sh,ps1} scripts hit different URLs for
        // Windows (zip) and Linux (tar.gz).
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ServeZip(ctx);
            return;
        }

        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            ServeTarGz(ctx);
            return;
        }

        // Unknown path → 404.
        ctx.Response.StatusCode = 404;
        ctx.Response.OutputStream.Close();
    }

    private void ServeZip(HttpListenerContext ctx)
    {
        var binaryName = _stagedBinaryFileName ?? "Squid.Tentacle.exe";
        var binaryContent = _stagedBinary ?? DefaultBinaryShim();

        var zipBytes = BuildZip(binaryName, binaryContent);

        ctx.Response.ContentType = "application/zip";
        ctx.Response.ContentLength64 = zipBytes.Length;
        ctx.Response.OutputStream.Write(zipBytes, 0, zipBytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private void ServeTarGz(HttpListenerContext ctx)
    {
        var binaryName = _stagedBinaryFileName ?? "squid-tentacle";
        var binaryContent = _stagedBinary ?? DefaultBinaryShim();

        var tarGzBytes = BuildTarGz(binaryName, binaryContent);

        ctx.Response.ContentType = "application/gzip";
        ctx.Response.ContentLength64 = tarGzBytes.Length;
        ctx.Response.OutputStream.Write(tarGzBytes, 0, tarGzBytes.Length);
        ctx.Response.OutputStream.Close();
    }

    // ── Archive builders ────────────────────────────────────────────────────

    private static byte[] BuildZip(string binaryName, byte[] binaryContent)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(binaryName, CompressionLevel.Fastest);
            using var entryStream = entry.Open();
            entryStream.Write(binaryContent, 0, binaryContent.Length);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Minimal POSIX-tar (ustar) builder + gzip wrapper. Real tar tooling is
    /// not in System.IO.Compression, but a single-entry ustar archive is
    /// straightforward to hand-write — 512-byte header + content blocks
    /// padded to 512 bytes + 1024-byte zero terminator. The install-
    /// tentacle.sh script only does <c>tar xzf</c> which accepts this format.
    /// </summary>
    private static byte[] BuildTarGz(string binaryName, byte[] binaryContent)
    {
        var tar = BuildTar(binaryName, binaryContent);

        using var gzMs = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(gzMs, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(tar, 0, tar.Length);
        }

        return gzMs.ToArray();
    }

    private static byte[] BuildTar(string fileName, byte[] content)
    {
        // ustar header: 512 bytes
        var header = new byte[512];

        // Name (100 bytes, NUL-terminated)
        var nameBytes = Encoding.ASCII.GetBytes(fileName);
        Array.Copy(nameBytes, header, Math.Min(nameBytes.Length, 100));

        // Mode (8 bytes, octal "0000644")
        WriteOctal(header, offset: 100, length: 8, value: 0x1ED);   // 0755

        // Uid (8), Gid (8)
        WriteOctal(header, 108, 8, 0);
        WriteOctal(header, 116, 8, 0);

        // Size (12 bytes, octal)
        WriteOctal(header, 124, 12, content.Length);

        // Mtime (12 bytes, octal — current time)
        WriteOctal(header, 136, 12, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Checksum field initially space-filled, computed below.
        for (var i = 148; i < 156; i++) header[i] = (byte)' ';

        // Type flag: '0' = regular file
        header[156] = (byte)'0';

        // Magic "ustar" + version "00"
        var ustar = Encoding.ASCII.GetBytes("ustar");
        Array.Copy(ustar, 0, header, 257, ustar.Length);
        header[263] = (byte)'0';
        header[264] = (byte)'0';

        // Checksum: sum of all header bytes (with checksum field as spaces)
        var checksum = 0;
        foreach (var b in header) checksum += b;
        WriteOctal(header, 148, 7, checksum);
        header[155] = 0;   // NUL terminator after checksum

        // Pad content to 512-byte block.
        var paddedSize = (content.Length + 511) / 512 * 512;
        var content512 = new byte[paddedSize];
        Array.Copy(content, content512, content.Length);

        // 1024-byte zero terminator.
        var terminator = new byte[1024];

        var tar = new byte[header.Length + content512.Length + terminator.Length];
        Array.Copy(header, 0, tar, 0, header.Length);
        Array.Copy(content512, 0, tar, header.Length, content512.Length);
        Array.Copy(terminator, 0, tar, header.Length + content512.Length, terminator.Length);

        return tar;
    }

    private static void WriteOctal(byte[] buffer, int offset, int length, long value)
    {
        var s = Convert.ToString(value, 8).PadLeft(length - 1, '0');
        var bytes = Encoding.ASCII.GetBytes(s);
        Array.Copy(bytes, 0, buffer, offset, Math.Min(bytes.Length, length - 1));
        buffer[offset + length - 1] = 0;   // NUL terminator
    }

    private static byte[] DefaultBinaryShim()
        => Encoding.UTF8.GetBytes("# fake binary shim — install-script E2E test only\n");

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static int GetEphemeralPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
