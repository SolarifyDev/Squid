using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    /// <summary>
    /// Pre-built archive bytes staged directly. When set, the mirror serves
    /// THESE bytes verbatim for every <c>.zip</c> / <c>.tar.gz</c> request
    /// — bypasses the auto-wrap-single-binary path that <see cref="StageBinary"/>
    /// follows. Used by full-bundle tests (e.g. J.E.3 upgrade lifecycle) that
    /// need to supply a multi-entry archive (binary tree + version files +
    /// `Squid.Tentacle.exe` placeholder) mirroring the production GitHub
    /// release zip's shape. <see cref="StageBinary"/> would double-wrap such
    /// content because it treats the supplied bytes as a single file inside
    /// a fresh zip — this field is the explicit "no wrapping, this IS the
    /// archive" seam.
    /// </summary>
    private byte[] _preBuiltArchive;
    private readonly HashSet<string> _notFoundVersions = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Per-archive byte cache. <see cref="ZipArchive"/> + GZipStream both
    /// embed timestamps in entry headers, so a fresh build for the
    /// <c>.zip</c> request and a separate fresh build for the <c>.sha256</c>
    /// companion request would produce different bytes → SHA mismatch
    /// false-positive in the production .ps1's verify step. Cache by URL
    /// path so every consumer of the same archive sees identical bytes
    /// (and the companion's hash matches).
    /// </summary>
    private readonly Dictionary<string, byte[]> _archiveBytesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _archiveCacheLock = new();
    /// <summary>
    /// Override for the <c>.sha256</c> companion content. When set, the
    /// mirror serves THIS string as the companion body instead of computing
    /// the actual SHA256 of the served archive. Used by the SHA-mismatch
    /// E2E test (J.E.3 / E12.u1) to inject a deliberately-wrong digest so
    /// the .ps1's <c>Get-FileHash</c> compare path triggers an exit 7.
    /// Null = serve the real computed SHA (happy-path coverage).
    /// </summary>
    private string _sha256Override;
    /// <summary>
    /// When true, the mirror returns 404 for every <c>.sha256</c> path even
    /// if the underlying archive is staged. Tests the production .ps1's
    /// "skip-with-warning" fallback for older releases / private mirrors that
    /// haven't replicated companion files yet (J.E.3 / E12.u2 — not in this
    /// PR but the toggle is here for future use).
    /// </summary>
    private bool _suppressSha256;
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
    /// Stages a PRE-BUILT archive that the mirror serves verbatim — no
    /// auto-wrapping. Use this when a test needs the served zip / tarball
    /// to contain MULTIPLE entries (e.g. a full binary tree + version
    /// files + canonical exe placeholder mirroring the production GitHub
    /// release zip's shape).
    ///
    /// <para><b>Why a separate API from <see cref="StageBinary"/></b>: the
    /// existing <see cref="StageBinary"/> API was designed for the install-
    /// script E2E tests where a single binary file inside the archive
    /// suffices. The upgrade-lifecycle E2E (12.J.E.3) requires a
    /// realistic full bundle (recursive copy of a service exe's runtime
    /// tree + a top-level <c>Squid.Tentacle.exe</c> the .ps1 existence-
    /// check expects). Passing such a pre-built zip via <see cref="StageBinary"/>
    /// would double-wrap it (the mirror would treat the bytes as a single
    /// file content + auto-create a fresh zip wrapper). This API is the
    /// explicit "no auto-wrap, these bytes ARE the archive" seam.</para>
    ///
    /// <para>SHA256 companion responses still work — they hash the pre-
    /// built bytes (cached per URL path so the .zip and .sha256 stay in
    /// sync byte-for-byte; same cache the auto-wrap path uses).</para>
    /// </summary>
    public void StagePreBuiltArchive(byte[] preBuiltArchiveBytes)
    {
        ArgumentNullException.ThrowIfNull(preBuiltArchiveBytes);
        _preBuiltArchive = preBuiltArchiveBytes;
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

    /// <summary>
    /// Overrides the <c>.sha256</c> companion body. The mirror normally
    /// computes the real SHA256 of the staged archive on each request; this
    /// override forces a fixed string instead so tests can inject a
    /// deliberately-wrong hash to exercise the production .ps1's exit-7
    /// (checksum failed) path.
    ///
    /// <para><b>Format mirrors <c>sha256sum</c></b>: <c>"&lt;64 hex&gt;  &lt;filename&gt;"</c>.
    /// The .ps1's parser splits on whitespace and takes the first token, so
    /// the suffix is cosmetic — but matching the canonical format keeps the
    /// fixture realistic for any future test that asserts on the wire body
    /// shape.</para>
    /// </summary>
    public void StageSha256Override(string sha256Body)
    {
        _sha256Override = sha256Body;
    }

    /// <summary>
    /// When set, every <c>.sha256</c> URL returns 404 regardless of what's
    /// staged for the underlying archive. Exercises the .ps1's
    /// skip-with-warning fallback (release without companion file).
    /// </summary>
    public void SuppressSha256Companion()
    {
        _suppressSha256 = true;
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
        // Windows (zip) and Linux (tar.gz). Companion `.sha256` files
        // are served alongside (production upgrade-windows-tentacle.ps1's
        // opportunistic verification path appends `.sha256` to DOWNLOAD_URL).
        if (path.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
        {
            ServeSha256(ctx, path);
            return;
        }

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

    /// <summary>
    /// Serves the SHA256 companion for the underlying archive. Production
    /// upgrade-windows-tentacle.ps1 fetches <c>$DOWNLOAD_URL.sha256</c>
    /// after a successful zip download; the body must be in
    /// <c>sha256sum</c> format (<c>"&lt;64-hex&gt;  &lt;filename&gt;"</c>).
    /// </summary>
    private void ServeSha256(HttpListenerContext ctx, string companionPath)
    {
        if (_suppressSha256)
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.OutputStream.Close();
            return;
        }

        // The companion's SHA target is the path with `.sha256` stripped —
        // mirror computes the SHA of whatever archive WOULD be served at
        // that path. Routed through the same cache the zip/tar.gz handlers
        // use so the digest matches byte-for-byte what the .ps1 just
        // downloaded (timestamps in archive headers are non-deterministic
        // across separate builds).
        var archivePath = companionPath.Substring(0, companionPath.Length - ".sha256".Length);
        var archiveFileName = Path.GetFileName(archivePath);

        if (!archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            !archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.OutputStream.Close();
            return;
        }

        string body;
        if (_sha256Override != null)
        {
            // Override path — used by SHA-mismatch tests to inject a
            // deliberately-wrong digest. Body still mimics sha256sum
            // format so the .ps1's parser handles it identically.
            body = _sha256Override;
        }
        else
        {
            var archiveBytes = GetOrBuildArchiveBytes(archivePath);
            var hash = SHA256.HashData(archiveBytes);
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            body = $"{hex}  {archiveFileName}\n";
        }

        var bodyBytes = Encoding.ASCII.GetBytes(body);
        ctx.Response.ContentType = "text/plain; charset=ascii";
        ctx.Response.ContentLength64 = bodyBytes.Length;
        ctx.Response.OutputStream.Write(bodyBytes, 0, bodyBytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private void ServeZip(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? string.Empty;
        var zipBytes = GetOrBuildArchiveBytes(path);

        ctx.Response.ContentType = "application/zip";
        ctx.Response.ContentLength64 = zipBytes.Length;
        ctx.Response.OutputStream.Write(zipBytes, 0, zipBytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private void ServeTarGz(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? string.Empty;
        var tarGzBytes = GetOrBuildArchiveBytes(path);

        ctx.Response.ContentType = "application/gzip";
        ctx.Response.ContentLength64 = tarGzBytes.Length;
        ctx.Response.OutputStream.Write(tarGzBytes, 0, tarGzBytes.Length);
        ctx.Response.OutputStream.Close();
    }

    /// <summary>
    /// Returns the cached bytes for a given archive URL path, building on
    /// first request. Caching is required because ZipArchive + GZipStream
    /// embed timestamps in entry headers — a separate build for the .zip
    /// request and the .sha256 companion request would produce different
    /// bytes and fail the production .ps1's hash verify.
    /// </summary>
    private byte[] GetOrBuildArchiveBytes(string archivePath)
    {
        lock (_archiveCacheLock)
        {
            if (_archiveBytesCache.TryGetValue(archivePath, out var cached))
                return cached;

            byte[] bytes;

            // Pre-built archive takes precedence — caller supplied a fully-
            // shaped multi-entry zip / tarball. Serve verbatim, no wrapping.
            if (_preBuiltArchive != null)
            {
                bytes = _preBuiltArchive;
            }
            else
            {
                // Auto-wrap-single-binary path (StageBinary). The default
                // when only a single file's content has been staged.
                var binaryContent = _stagedBinary ?? DefaultBinaryShim();

                if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var binaryName = _stagedBinaryFileName ?? "Squid.Tentacle.exe";
                    bytes = BuildZip(binaryName, binaryContent);
                }
                else if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                {
                    var binaryName = _stagedBinaryFileName ?? "squid-tentacle";
                    bytes = BuildTarGz(binaryName, binaryContent);
                }
                else
                {
                    throw new InvalidOperationException($"Unsupported archive path: {archivePath}");
                }
            }

            _archiveBytesCache[archivePath] = bytes;
            return bytes;
        }
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
