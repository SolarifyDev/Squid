using System.Security.Cryptography;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>A pushed file as it sits in a temp file: where, how big and what its SHA-256 (lower-case hex) is.</summary>
public sealed record StagedUpload(string Path, long SizeBytes, string Sha256);

/// <summary>Copies an upload to a temp file while hashing it and enforcing the size limit, reading the stream once and never holding the file in memory.</summary>
public static class UploadStager
{
    private const int BufferSize = 81920;

    public static async Task<StagedUpload> StageAsync(Stream content, long maxBytes, long? declaredSizeBytes, CancellationToken ct)
    {
        if (declaredSizeBytes > maxBytes)
            throw new PackageTooLargeException(maxBytes);

        var path = Path.Combine(Path.GetTempPath(), $"squid-builtin-push-{Guid.NewGuid():N}.upload");

        try
        {
            return await CopyAsync(content, path, maxBytes, ct).ConfigureAwait(false);
        }
        catch
        {
            File.Delete(path);

            throw;
        }
    }

    private static async Task<StagedUpload> CopyAsync(Stream content, string path, long maxBytes, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[BufferSize];
        long total = 0;
        int read;

        while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;

            if (total > maxBytes)
                throw new PackageTooLargeException(maxBytes);

            hash.AppendData(buffer, 0, read);

            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        if (total == 0)
            throw new EmptyPackageUploadException();

        return new StagedUpload(path, total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
}
