using Halibut.Queue.MessageStreamWrapping;

namespace Squid.Core.Halibut.RedisQueue.Encryption;

/// <summary>
/// Halibut serialises a queued request or response to BSON and runs the bytes through these wrappers before they go into Redis, and back on the way out. This one
/// seals them, so what Redis holds (including the password of an encrypted file and every argument of an RPC) is never plaintext. It is installed only on the
/// queue's serializer, never on the wire protocol, which agents speak unchanged. The data streams (package bytes, variables) are not in the message: they go to
/// the shared database store, which seals them itself.
/// </summary>
public sealed class QueueMessageStreamWrapper(IQueueMessageKeys keys) : IMessageStreamWrapper
{
    public Stream WrapMessageSerialisationStream(Stream stream) => new SealOnDisposeStream(stream, keys);

    public Stream WrapMessageDeserialisationStream(Stream stream)
    {
        using var sealedMessage = new MemoryStream();

        stream.CopyTo(sealedMessage);

        var bytes = sealedMessage.ToArray();

        return new MemoryStream(QueueMessageEnvelope.Open(bytes, keys.Resolve(QueueMessageEnvelope.WrappedKeyOf(bytes))), writable: false);
    }

    /// <summary>Collects what the serializer writes and, when Halibut disposes it (the serializer has flushed by then), writes the sealed message to the underlying stream, which it leaves open.</summary>
    private sealed class SealOnDisposeStream(Stream inner, IQueueMessageKeys keys) : Stream
    {
        private readonly MemoryStream _plain = new();
        private bool _sealed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => _plain.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => _plain.Write(buffer);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_sealed)
            {
                _sealed = true;

                var (key, wrapped) = keys.Current();
                var message = QueueMessageEnvelope.Seal(_plain.GetBuffer().AsSpan(0, (int)_plain.Length), key, wrapped);

                inner.Write(message, 0, message.Length);

                _plain.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
