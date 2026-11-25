using System.Security.Cryptography;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;

namespace Squid.Core.Services.DeploymentExecution.Packages.Sources.BuiltIn;

/// <summary>Passes a stored file through while hashing it, and fails the read that reaches the end if the bytes are not the ones recorded (size and SHA-256).</summary>
internal sealed class VerifyingReadStream(Stream inner, string packageId, string version, long expectedSize, string expectedSha256) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => expectedSize;
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Consume(inner.Read(buffer, offset, count), buffer.AsSpan(offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        return Consume(read, buffer.Span);
    }

    private int Consume(int read, ReadOnlySpan<byte> data)
    {
        if (read > 0)
        {
            _read += read;
            _hash.AppendData(data[..read]);

            return read;
        }

        var actual = Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

        if (_read != expectedSize || actual != expectedSha256)
            throw new BuiltInPackageIntegrityException(packageId, version, expectedSha256, actual);

        return 0;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
