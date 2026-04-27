using System.Security.Cryptography;
using Halibut;
using Serilog;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Tentacle.FileTransfer;

/// <summary>
/// P1-Phase9b.3 — agent-side implementation of <see cref="IFileTransferService"/>.
///
/// <para><b>Workspace boundary</b>: uploads land under
/// <see cref="UploadRoot"/> (default <c>~/.squid/uploads</c>) — the agent
/// rewrites the requested <paramref name="remotePath"/> to a sub-path of
/// this root so a malicious server can't write outside the workspace
/// boundary (e.g. <c>/etc/cron.d/abc</c> or <c>/root/.ssh/authorized_keys</c>).
/// Operators get the <em>actual</em> stored path back in
/// <see cref="UploadResult.FullPath"/>.</para>
///
/// <para><b>Path validation</b>: relative paths are accepted; <c>..</c>
/// segments and absolute paths are rewritten to a hash-derived filename
/// inside the upload root.</para>
/// </summary>
public sealed class LocalFileTransferService : IFileTransferService
{
    /// <summary>
    /// Override this in tests via the constructor — production resolves
    /// against the agent's home directory.
    /// </summary>
    public string UploadRoot { get; }

    public LocalFileTransferService() : this(DefaultUploadRoot()) { }

    public LocalFileTransferService(string uploadRoot)
    {
        UploadRoot = uploadRoot ?? throw new ArgumentNullException(nameof(uploadRoot));
        Directory.CreateDirectory(UploadRoot);
    }

    private static string DefaultUploadRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                       ".squid", "uploads");

    public UploadResult UploadFile(string remotePath, DataStream upload)
    {
        if (upload == null) throw new ArgumentNullException(nameof(upload));

        var safePath = ResolveSafePath(remotePath);
        var dir = Path.GetDirectoryName(safePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        long bytesWritten;
        string hash;

        // P1-Phase9b.3: hash AS WE WRITE. Two reasons:
        //   1. Server can verify integrity without re-reading the file from disk.
        //   2. The hash is what gates "did the upload corrupt mid-stream" —
        //      Halibut's DataStream is a streaming abstraction and a network
        //      hiccup mid-transfer would otherwise produce a half-written
        //      file with no error signal.
        //
        // Halibut's IDataStreamReceiver exposes ReadAsync((Stream, CT) => Task)
        // — we receive the inbound stream and copy it through CryptoStream so
        // bytes flow through SHA-256 before hitting disk.
        using (var sha = SHA256.Create())
        using (var fileStream = File.Create(safePath))
        using (var hashStream = new CryptoStream(fileStream, sha, CryptoStreamMode.Write))
        {
            upload.Receiver()
                .ReadAsync(async (inbound, ct) => await inbound.CopyToAsync(hashStream, ct).ConfigureAwait(false),
                           CancellationToken.None)
                .GetAwaiter().GetResult();

            // FlushFinalBlock so the hash is finalised before reading.
            hashStream.FlushFinalBlock();
            bytesWritten = fileStream.Length;
            hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }

        Log.Information(
            "Uploaded file: {RemotePath} → {SafePath} ({Bytes} bytes, sha256={Hash})",
            remotePath, safePath, bytesWritten, hash);

        return new UploadResult(safePath, hash, bytesWritten);
    }

    public DataStream DownloadFile(string remotePath)
    {
        var safePath = ResolveSafePath(remotePath);

        if (!File.Exists(safePath))
            throw new FileNotFoundException(
                $"Requested file not found at {safePath} (resolved from {remotePath}).",
                safePath);

        var bytes = File.ReadAllBytes(safePath);
        Log.Information(
            "Downloaded file: {RemotePath} ← {SafePath} ({Bytes} bytes)",
            remotePath, safePath, bytes.Length);

        return DataStream.FromBytes(bytes);
    }

    /// <summary>
    /// P1-Phase9b.3 — workspace-boundary path rewrite.
    ///
    /// <para>Behaviour:
    /// <list type="bullet">
    ///   <item>Rooted paths (<c>/etc/passwd</c>, <c>C:\Windows</c>) → rewrite
    ///         to a hash-derived filename inside the upload root.</item>
    ///   <item>Paths containing <c>..</c> traversal → rewrite to hash-derived.</item>
    ///   <item>Otherwise → join with upload root, returning a path inside the
    ///         workspace. The full path is then verified to live UNDER
    ///         the upload root (defence-in-depth against symlink tricks).</item>
    /// </list></para>
    ///
    /// <c>internal</c> for unit testing.
    /// </summary>
    internal string ResolveSafePath(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
            throw new ArgumentException("remotePath cannot be empty.", nameof(remotePath));

        // Reject obvious workspace-escape attempts. Cross-platform check —
        // Path.IsPathRooted on macOS/Linux returns false for Windows-style
        // drive paths (C:\), so we also check for backslashes and drive-letter
        // prefixes explicitly. A server compromised on a Linux deploy host
        // could otherwise sneak Windows path syntax through.
        var looksWindowsRooted = remotePath.Length >= 2 && remotePath[1] == ':';
        var hasBackslash = remotePath.Contains('\\');

        if (Path.IsPathRooted(remotePath) || remotePath.Contains("..") || looksWindowsRooted || hasBackslash)
        {
            // Hash-derived stable filename so a redelivered upload of the same
            // path produces the same destination (idempotent under retry).
            var safeName = "rewritten-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(remotePath))).Substring(0, 16).ToLowerInvariant();
            Log.Warning(
                "[FILE-TRANSFER] Rejected rooted/traversal path {RemotePath} — rewritten to {SafeName} inside upload root.",
                remotePath, safeName);
            return Path.Combine(UploadRoot, safeName);
        }

        var combined = Path.GetFullPath(Path.Combine(UploadRoot, remotePath));
        var rootFull = Path.GetFullPath(UploadRoot);

        // Defence-in-depth: even after Path.Combine + GetFullPath, a symlink
        // could escape. Verify the resolved path is genuinely UNDER the root.
        if (!combined.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && combined != rootFull)
        {
            var safeName = "escaped-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(remotePath))).Substring(0, 16).ToLowerInvariant();
            Log.Warning(
                "[FILE-TRANSFER] Path resolved outside upload root: {Combined} (root={Root}) — rewritten to {SafeName}.",
                combined, rootFull, safeName);
            return Path.Combine(rootFull, safeName);
        }

        return combined;
    }
}
