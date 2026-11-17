using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Squid.Core.Services.OctopusImport.SessionFiles;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.SessionFiles;

public class OctopusImportSessionFileCodecTests
{
    private static readonly Guid SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    // ── Persisted formats ───────────────────────────────────────────────────

    // Rows written by one Squid version are read by the next: every name and size stored with a file is pinned.
    [Fact]
    public void PersistedFormatConstants_Pinned()
    {
        OctopusImportSessionFileCodec.ChunkBytes.ShouldBe(1024 * 1024);
        OctopusImportSessionFileCodec.KeyBytes.ShouldBe(32);
        OctopusImportSessionFileCodec.AesGcmV1.ShouldBe("aes-gcm-v1");
        OctopusImportSessionFileCodec.None.ShouldBe("none");
        OctopusImportSessionFileCodec.Identity.ShouldBe("identity");
        OctopusImportSessionFileCodec.Deflate.ShouldBe("deflate");
        OctopusImportSessionFilePaths.StagingPrefix.ShouldBe("history-staging/");
        OctopusImportSessionFilePaths.DetailsPrefix.ShouldBe("history-details/");
        OctopusImportSessionFilePaths.Marker.ShouldBe("staging.id");
        OctopusImportSessionFileKeys.PurposeTag.ShouldBe("squid.octopus-import.session-file-key.v1\n");
        OctopusImportSessionFileKeys.KdfScope.ShouldBe(-1002);
        Encoding.UTF8.GetString(OctopusImportSessionFileCodec.AssociatedData(SessionId, "a/b.json", 3)).ShouldBe("11111111222233334444555555555555|a/b.json|3");
    }

    // ── Codec ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OctopusImportSessionFileEncoding.Identity, true)]
    [InlineData(OctopusImportSessionFileEncoding.Identity, false)]
    [InlineData(OctopusImportSessionFileEncoding.Deflate, true)]
    [InlineData(OctopusImportSessionFileEncoding.Deflate, false)]
    public void Encode_Decode_RoundTrips(OctopusImportSessionFileEncoding encoding, bool sealedWithKey)
    {
        var plain = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"Id\":\"Releases-1\",\"Version\":\"1.0.0\"}", 50)));
        var key = sealedWithKey ? Key : null;

        var stored = Encode(plain, key, encoding, "history-staging/Release/x.json", 0);

        OctopusImportSessionFileCodec.Decode(stored, key, OctopusImportSessionFileCodec.EncryptionName(key), OctopusImportSessionFileCodec.EncodingName(encoding), SessionId, "history-staging/Release/x.json", 0).ShouldBe(plain);
    }

    // The export upload holds secrets: what is stored is never the plaintext, and a stored chunk is only its own, at its own place.
    [Fact]
    public void Sealed_StoredBytesNeverContainThePlaintext()
    {
        var plain = Encoding.UTF8.GetBytes("password=marker-6f1c2e");

        var stored = Encode(plain, Key, OctopusImportSessionFileEncoding.Identity, "export.zip", 0);

        Encoding.UTF8.GetString(stored).ShouldNotContain("marker-6f1c2e");
    }

    [Theory]
    [InlineData("other-index")]
    [InlineData("other-path")]
    [InlineData("other-session")]
    [InlineData("tampered")]
    [InlineData("other-key")]
    public void Sealed_ChunkReadAnywhereElseOrChanged_FailsAuthentication(string change)
    {
        var stored = Encode(Encoding.UTF8.GetBytes("chunk"), Key, OctopusImportSessionFileEncoding.Identity, "export.zip", 1);
        var (sessionId, path, index, key) = (SessionId, "export.zip", 1, Key);

        switch (change)
        {
            case "other-index": index = 0; break;
            case "other-path": path = "octopus-history.zip"; break;
            case "other-session": sessionId = Guid.NewGuid(); break;
            case "tampered": stored[^1] ^= 1; break;
            default: key = Enumerable.Repeat((byte)7, 32).ToArray(); break;
        }

        Should.Throw<CryptographicException>(() => OctopusImportSessionFileCodec.Decode(stored, key, OctopusImportSessionFileCodec.AesGcmV1, OctopusImportSessionFileCodec.Identity, sessionId, path, index));
    }

    // A sealed file is never read without its key, never as "unsealed".
    [Fact]
    public void Sealed_ReadWithoutTheKey_Fails()
    {
        var stored = Encode(Encoding.UTF8.GetBytes("chunk"), Key, OctopusImportSessionFileEncoding.Identity, "export.zip", 0);

        Should.Throw<CryptographicException>(() => OctopusImportSessionFileCodec.Decode(stored, null, OctopusImportSessionFileCodec.AesGcmV1, OctopusImportSessionFileCodec.Identity, SessionId, "export.zip", 0));
    }

    [Fact]
    public void Deflate_StagedJsonStoredSmallerThanItself()
    {
        var plain = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 200).Select(n => $"{{\"Id\":\"Deployments-{n}\",\"EnvironmentId\":\"Environments-1\",\"ChannelId\":\"Channels-1\"}}")));

        Encode(plain, Key, OctopusImportSessionFileEncoding.Deflate, "p", 0).Length.ShouldBeLessThan(plain.Length / 2);
    }

    // ── Chunker ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 1)]
    [InlineData(OctopusImportSessionFileCodec.ChunkBytes, 1)]
    [InlineData(OctopusImportSessionFileCodec.ChunkBytes + 1, 2)]
    [InlineData(3 * OctopusImportSessionFileCodec.ChunkBytes, 3)]
    public async Task Chunker_CutsWholeChunks_HashesAndCountsTheOriginalBytes(int length, int expectedChunks)
    {
        var plain = RandomNumberGenerator.GetBytes(length);
        var decoded = new MemoryStream();

        using var encoder = new OctopusImportSessionFileChunkEncoder(SessionId, "f", Key, OctopusImportSessionFileEncoding.Identity);
        using var chunker = new OctopusImportSessionFileChunker(new MemoryStream(plain), encoder);

        while (await chunker.NextAsync(CancellationToken.None) is { } chunk)
            decoded.Write(OctopusImportSessionFileCodec.Decode(chunk.Span, Key, OctopusImportSessionFileCodec.AesGcmV1, OctopusImportSessionFileCodec.Identity, SessionId, "f", chunker.Count - 1));

        (chunker.Completed, chunker.Count, chunker.Length).ShouldBe((true, expectedChunks, (long)length));
        chunker.Sha256.ShouldBe(Convert.ToHexString(SHA256.HashData(plain)).ToLowerInvariant());
        decoded.ToArray().ShouldBe(plain);
    }

    // The 258 MB bundle of a large space is stored through the chunker: one chunk in memory at a time, never the whole file.
    [Fact]
    public async Task Chunker_LargeFile_AllocatesAboutOneChunk_NotTheFile()
    {
        const long length = 64L * 1024 * 1024;
        using var encoder = new OctopusImportSessionFileChunkEncoder(SessionId, "octopus-history.zip", Key, OctopusImportSessionFileEncoding.Identity);
        using var chunker = new OctopusImportSessionFileChunker(new ZeroStream(length), encoder);

        await chunker.NextAsync(CancellationToken.None);
        var before = GC.GetAllocatedBytesForCurrentThread();

        while (await chunker.NextAsync(CancellationToken.None) is not null) { }

        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBeLessThan(8L * 1024 * 1024);
        chunker.Length.ShouldBe(length);
    }

    // ── Session key ─────────────────────────────────────────────────────────

    [Fact]
    public void Key_WrapThenUnwrap_RoundTrips_AndTheEnvelopeHidesIt()
    {
        var encryption = RealEncryption();
        var key = OctopusImportSessionFileKeys.NewKey();

        var wrapped = OctopusImportSessionFileKeys.Wrap(encryption, key);

        wrapped.ShouldNotContain(Convert.ToBase64String(key));
        OctopusImportSessionFileKeys.UnwrapOrNull(encryption, wrapped).ShouldBe(key);
    }

    // An envelope written for any other purpose under the same master key (an export password, a variable) never unwraps as a file key.
    [Theory]
    [InlineData("export-password")]
    [InlineData("not-an-envelope")]
    [InlineData("other-master-key")]
    public void Key_EnvelopeNotAFileKey_Refused(string envelope)
    {
        var encryption = RealEncryption();
        var wrapped = envelope switch
        {
            "export-password" => encryption.EncryptAsync("squid.octopus-import.export-password.v1\nsecret", OctopusImportSessionFileKeys.KdfScope),
            "not-an-envelope" => Convert.ToBase64String(Key),
            _ => OctopusImportSessionFileKeys.Wrap(RealEncryption(9), Key)
        };

        OctopusImportSessionFileKeys.UnwrapOrNull(encryption, wrapped).ShouldBeNull();
    }

    private static byte[] Encode(byte[] plain, byte[] key, OctopusImportSessionFileEncoding encoding, string path, int index)
    {
        using var encoder = new OctopusImportSessionFileChunkEncoder(SessionId, path, key, encoding);

        return encoder.Encode(plain, index).ToArray();
    }

    private static VariableEncryptionService RealEncryption(byte seed = 1)
    {
        var masterKey = Enumerable.Range(0, 32).Select(i => (byte)(i + seed)).ToArray();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Security:VariableEncryption:MasterKey"] = Convert.ToBase64String(masterKey) }).Build();

        return new VariableEncryptionService(new SecuritySetting(config));
    }

    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - _position);

            Array.Clear(buffer, offset, read);
            _position += read;

            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = (int)Math.Min(buffer.Length, length - _position);

            buffer.Span[..read].Clear();
            _position += read;

            return ValueTask.FromResult(read);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
