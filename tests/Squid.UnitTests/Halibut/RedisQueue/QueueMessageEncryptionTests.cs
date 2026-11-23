using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Halibut;
using Halibut.Diagnostics;
using Halibut.Queue;
using Halibut.Queue.MessageStreamWrapping;
using Halibut.Transport.Protocol;
using Moq;
using Squid.Core.Halibut.DataStreamStorage;
using Squid.Core.Halibut.RedisQueue;
using Squid.Core.Halibut.RedisQueue.Encryption;
using Squid.Core.Services.Security;
using Squid.Message.Contracts.Tentacle;
using static Squid.UnitTests.Halibut.DataStreamStorage.DataStreamStorageTestSupport;
using static Squid.UnitTests.Halibut.RedisQueue.RedisQueueTestSupport;

namespace Squid.UnitTests.Halibut.RedisQueue;

public class QueueMessageEnvelopeTests
{
    private static readonly byte[] Key = Bytes(32, 3);
    private const string Wrapped = "v2:wrapped-key-envelope";

    [Fact]
    public void SealedMessage_OpensToTheOriginal_AndHoldsNoPlaintext()
    {
        var plain = Encoding.UTF8.GetBytes("password=hunter2-MARKER;" + new string('x', 200));

        var sealedMessage = QueueMessageEnvelope.Seal(plain, Key, Wrapped);

        QueueMessageEnvelope.Open(sealedMessage, Key).ShouldBe(plain);
        sealedMessage.AsSpan().IndexOf("hunter2-MARKER"u8).ShouldBe(-1, "the secret must not be readable in what goes into Redis");
        QueueMessageEnvelope.WrappedKeyOf(sealedMessage).ShouldBe(Wrapped);
    }

    [Fact]
    public void TheSameMessageSealedTwice_DiffersEveryTime()
    {
        var plain = Bytes(64);

        QueueMessageEnvelope.Seal(plain, Key, Wrapped).ShouldNotBe(QueueMessageEnvelope.Seal(plain, Key, Wrapped), "a fresh random nonce per message");
    }

    [Fact]
    public void EmptyMessage_RoundTrips() => QueueMessageEnvelope.Open(QueueMessageEnvelope.Seal([], Key, Wrapped), Key).ShouldBeEmpty();

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(-1)]
    [InlineData(-20)]
    public void AnyChangedByte_IsRefused(int index)
    {
        var sealedMessage = QueueMessageEnvelope.Seal(Bytes(100), Key, Wrapped);
        var at = index >= 0 ? index : sealedMessage.Length + index;

        sealedMessage[at] ^= 0x01;

        Should.Throw<QueueMessageUnreadableException>(() => QueueMessageEnvelope.Open(sealedMessage, Key));
    }

    [Fact]
    public void ChangedWrappedKeyName_IsRefused()
    {
        var sealedMessage = QueueMessageEnvelope.Seal(Bytes(100), Key, Wrapped);
        var inside = QueueMessageEnvelope.Magic.Length + sizeof(int) + 3;

        sealedMessage[inside] ^= 0x01;

        Should.Throw<QueueMessageUnreadableException>(() => QueueMessageEnvelope.Open(sealedMessage, Key));
    }

    [Fact]
    public void AnotherKey_IsRefused() => Should.Throw<QueueMessageUnreadableException>(() => QueueMessageEnvelope.Open(QueueMessageEnvelope.Seal(Bytes(100), Key, Wrapped), Bytes(32, 9)));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(40)]
    public void TruncatedOrForeignBytes_AreRefused(int length) => Should.Throw<QueueMessageUnreadableException>(() => QueueMessageEnvelope.Open(Bytes(length), Key));

    [Fact]
    public void WireFormat_IsPinned()
    {
        QueueMessageEnvelope.Magic.ShouldBe("SQQ1");
        QueueMessageEnvelope.KeyBytes.ShouldBe(32);
        QueueMessageEnvelope.NonceBytes.ShouldBe(12);
        QueueMessageEnvelope.TagBytes.ShouldBe(16);
        QueueMessageEnvelope.Seal(new byte[5], Key, "k").Length.ShouldBe(4 + 4 + 1 + 12 + 16 + 5);
    }
}

public class QueueMessageKeysTests
{
    private readonly ManualTimeProvider _clock = new();
    private readonly Mock<IQueueMessageKeyProtector> _protector = new();
    private int _wraps;

    public QueueMessageKeysTests()
    {
        _protector.Setup(p => p.Wrap(It.IsAny<byte[]>())).Returns<byte[]>(key => $"wrapped-{Interlocked.Increment(ref _wraps)}-{Convert.ToHexString(key)}");
        _protector.Setup(p => p.Unwrap(It.IsAny<string>())).Returns<string>(w => Convert.FromHexString(w[(w.LastIndexOf('-') + 1)..]));
    }

    private QueueMessageKeys Keys() => new(_protector.Object, Options(), _clock);

    [Fact]
    public void ManyMessages_ShareOneWrappedKey_UntilItRotates()
    {
        var keys = Keys();
        var first = keys.Current();

        for (var i = 0; i < 100; i++) keys.Current().Wrapped.ShouldBe(first.Wrapped);

        _wraps.ShouldBe(1, "wrapping is a 600k-iteration PBKDF2: once per rotation, not per message");

        _clock.Advance(TimeSpan.FromMinutes(10));

        var rotated = keys.Current();

        rotated.Wrapped.ShouldNotBe(first.Wrapped);
        rotated.Key.ShouldNotBe(first.Key);
        _wraps.ShouldBe(2);
    }

    [Fact]
    public void AKeyThisPodMade_IsResolvedWithoutUnwrapping()
    {
        var keys = Keys();
        var current = keys.Current();

        keys.Resolve(current.Wrapped).ShouldBe(current.Key);

        _protector.Verify(p => p.Unwrap(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void AKeyAnotherPodMade_IsUnwrappedOnce()
    {
        var other = new QueueMessageKeys(_protector.Object, Options(), _clock).Current();
        var keys = Keys();

        keys.Resolve(other.Wrapped).ShouldBe(other.Key);
        keys.Resolve(other.Wrapped).ShouldBe(other.Key);

        _protector.Verify(p => p.Unwrap(other.Wrapped), Times.Once);
    }

    [Fact]
    public void WrapFailing_ThrowsAndIsRetriedNextTime_NothingIsSealedUnprotected()
    {
        var fail = true;

        _protector.Setup(p => p.Wrap(It.IsAny<byte[]>())).Returns<byte[]>(_ => fail ? throw new InvalidOperationException("no master key") : "wrapped");

        var keys = Keys();

        Should.Throw<InvalidOperationException>(() => keys.Current());

        fail = false;

        keys.Current().Wrapped.ShouldBe("wrapped");
    }
}

public class QueueMessageKeyProtectorTests
{
    private static QueueMessageKeyProtector Protector(byte seed = 1) => new(new Lazy<IVariableEncryptionService>(() => Encryption(seed)));

    [Fact]
    public void Constants_ArePinned()
    {
        QueueMessageKeyProtector.KdfScope.ShouldBe(-1004);
        QueueMessageKeyProtector.PurposeTag.ShouldBe("squid.halibut-queue-message-key.v1\n");
    }

    [Fact]
    public void WrappedKey_UnwrapsToTheSameKey_AndIsNotPlaintext()
    {
        var key = Bytes(32, 5);
        var wrapped = Protector().Wrap(key);

        wrapped.ShouldStartWith(VariableEncryptionService.EncryptionPrefixV2);
        wrapped.ShouldNotContain(Convert.ToBase64String(key));
        Protector().Unwrap(wrapped).ShouldBe(key);
    }

    [Fact]
    public void AnotherMasterKey_CannotUnwrap() => Should.Throw<QueueMessageUnreadableException>(() => Protector(2).Unwrap(Protector(1).Wrap(Bytes(32))));

    [Fact]
    public void ADataStreamKeyEnvelope_NeverUnwrapsAsAQueueKey()
        => Should.Throw<QueueMessageUnreadableException>(() => Protector().Unwrap(Keys().Wrap(Bytes(32))), "same master key, different purpose");

    [Theory]
    [InlineData("")]
    [InlineData("not-an-envelope")]
    public void NotAnEnvelope_IsRefused(string wrapped) => Should.Throw<QueueMessageUnreadableException>(() => Protector().Unwrap(wrapped));

    [Fact]
    public void NoMasterKey_FailsClosed_NothingIsWrapped()
    {
        var protector = new QueueMessageKeyProtector(new Lazy<IVariableEncryptionService>(() => throw new InvalidOperationException("no master key")));

        Should.Throw<InvalidOperationException>(() => protector.Wrap(Bytes(32)));
    }
}

/// <summary>Through Halibut's own serializer, so what is sealed is exactly what Halibut would put in Redis for a real request.</summary>
public class QueueMessageStreamWrapperTests
{
    private const string Secret = "SENSITIVE-FILE-PASSWORD-9f3a";

    private static (QueueMessageSerializer Serializer, QueueMessageKeys Keys) Build(byte seed = 1)
    {
        var keys = new QueueMessageKeys(Protector(seed), Options(), TimeProvider.System);

        return (SerializerOf(keys), keys);
    }

    /// <summary>The serializer a real runtime hands the queue factory, built the way the module builds it: the contract registry and the sealing wrapper on the queue only.</summary>
    private static QueueMessageSerializer SerializerOf(IQueueMessageKeys keys)
    {
        QueueMessageSerializer captured = null;

        using var rsa = RSA.Create(2048);
        using var cert = new CertificateRequest("CN=serializer-probe", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        var runtime = new HalibutRuntimeBuilder()
            .WithServerCertificate(X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, string.Empty), string.Empty))
            .WithTypeRegistry(HalibutQueueWiring.BuildContractRegistry())
            .WithQueueMessageStreamWrappers(new MessageStreamWrappers([new QueueMessageStreamWrapper(keys)]))
            .WithPendingRequestQueueFactory(serializer =>
            {
                captured = serializer;

                return new FakeRedisFactory(new FakeRedisQueue());
            })
            .Build();

        runtime.Dispose();

        return captured;
    }

    private static QueueMessageKeyProtector Protector(byte seed) => new(new Lazy<IVariableEncryptionService>(() => Encryption(seed)));

    private static RequestMessage ScriptRequest()
    {
        var request = Request("StartScript");
        var file = new ScriptFile("sensitiveVariables.json", DataStream.FromBytes(Bytes(64)), Secret);

        request.Params = [new StartScriptCommand(new ScriptTicket("t-1"), "echo hi", ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(1), null, [], "task-1", TimeSpan.FromMinutes(1), [file])];

        return request;
    }

    [Fact]
    public async Task ARequestCarryingASecret_RoundTripsThroughTheSealedQueueFormat_AndTheSecretIsNotInTheStoredBytes()
    {
        var (serializer, _) = Build();
        var request = ScriptRequest();

        var (stored, streams) = await serializer.WriteMessage(request);

        stored.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Secret)).ShouldBe(-1, "the file password sits in the message and must be sealed");
        stored.AsSpan().IndexOf("echo hi"u8).ShouldBe(-1);
        streams.Count.ShouldBe(1, "the file contents are a data stream, kept out of the message");

        var (read, _) = await serializer.ReadMessage<RequestMessage>(stored);
        var command = (StartScriptCommand)read.Params[0];

        read.Id.ShouldBe(request.Id);
        command.ScriptBody.ShouldBe("echo hi");
        command.Files.Single().EncryptionPassword.ShouldBe(Secret);
    }

    [Fact]
    public async Task APodWithADifferentKeyRing_ReadsTheMessage_BecauseTheWrappedKeyTravelsWithIt()
    {
        var (writer, _) = Build();
        var (reader, _) = Build();

        var (stored, _) = await writer.WriteMessage(ScriptRequest());
        var (read, _) = await reader.ReadMessage<RequestMessage>(stored);

        ((StartScriptCommand)read.Params[0]).Files.Single().EncryptionPassword.ShouldBe(Secret);
    }

    [Fact]
    public async Task AChangedStoredMessage_IsRefusedNotReadAsGarbage()
    {
        var (serializer, _) = Build();
        var (stored, _) = await serializer.WriteMessage(ScriptRequest());

        stored[^5] ^= 0x01;

        await Should.ThrowAsync<QueueMessageUnreadableException>(() => serializer.ReadMessage<RequestMessage>(stored));
    }

    [Fact]
    public async Task APodWithAnotherMasterKey_CannotReadTheMessage()
    {
        var (writer, _) = Build(1);
        var (reader, _) = Build(2);
        var (stored, _) = await writer.WriteMessage(ScriptRequest());

        await Should.ThrowAsync<QueueMessageUnreadableException>(() => reader.ReadMessage<RequestMessage>(stored));
    }

    [Fact]
    public async Task NoMasterKey_NothingIsWritten()
    {
        var keys = new QueueMessageKeys(new QueueMessageKeyProtector(new Lazy<IVariableEncryptionService>(() => throw new InvalidOperationException("no master key"))), Options(), TimeProvider.System);
        var serializer = SerializerOf(keys);

        var ex = await Should.ThrowAsync<AggregateException>(() => serializer.WriteMessage(ScriptRequest()));

        ex.InnerExceptions.ShouldContain(e => e is InvalidOperationException && e.Message.Contains("no master key"), "Halibut's disposal of the sealing stream reports the failure; no bytes are returned");
    }

    [Fact]
    public async Task AResponse_IsSealedToo()
    {
        var (serializer, _) = Build();
        var response = ResponseMessage.FromResult(Request(), new ScriptStatusResponse(new ScriptTicket("t"), ProcessState.Complete, 0, new List<ProcessOutput> { new(ProcessOutputSource.StdOut, Secret) }, 1));

        var (stored, _) = await serializer.WriteMessage(response);

        stored.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Secret)).ShouldBe(-1, "script output may carry secrets too");
        ((ScriptStatusResponse)(await serializer.ReadMessage<ResponseMessage>(stored)).Message.Result).Logs.Single().Text.ShouldBe(Secret);
    }
}
