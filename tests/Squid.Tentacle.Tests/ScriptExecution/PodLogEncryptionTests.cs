using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class PodLogEncryptionTests
{
    private static readonly byte[] TestMachineKey = System.Text.Encoding.UTF8.GetBytes("test-machine-key-for-unit-tests!");

    [Fact]
    public void DeriveKey_Deterministic()
    {
        var key1 = PodLogEncryption.DeriveLogEncryptionKey(TestMachineKey, "ticket-1");
        var key2 = PodLogEncryption.DeriveLogEncryptionKey(TestMachineKey, "ticket-1");

        key1.ShouldBe(key2);
    }

    [Fact]
    public void DeriveKey_DifferentTickets_DifferentKeys()
    {
        var key1 = PodLogEncryption.DeriveLogEncryptionKey(TestMachineKey, "ticket-1");
        var key2 = PodLogEncryption.DeriveLogEncryptionKey(TestMachineKey, "ticket-2");

        key1.ShouldNotBe(key2);
    }

    [Fact]
    public void DeriveKey_Returns32Bytes()
    {
        var key = PodLogEncryption.DeriveLogEncryptionKey(TestMachineKey, "ticket-1");

        key.Length.ShouldBe(32);
    }

    [Fact]
    public void EncryptLine_ProducesPrefix()
    {
        var key = PodLogEncryption.DeriveLogEncryptionKey(TestMachineKey, "ticket-1");
        var encrypted = PodLogEncryption.EncryptLine("hello world", key, 1);

        encrypted.ShouldStartWith("SQUID_ENC|");
    }

    [Fact]
    public void TryDecryptLine_RoundTrips()
    {
        var key = PodLogEncryption.DeriveLogEncryptionKey(TestMachineKey, "ticket-1");
        var encrypted = PodLogEncryption.EncryptLine("sensitive deployment output", key, 42);

        var (success, plaintext) = PodLogEncryption.TryDecryptLine(encrypted, key);

        success.ShouldBeTrue();
        plaintext.ShouldBe("sensitive deployment output");
    }

    [Fact]
    public void TryDecryptLine_Plaintext_ReturnsFalse()
    {
        var key = PodLogEncryption.DeriveLogEncryptionKey(TestMachineKey, "ticket-1");

        var (success, _) = PodLogEncryption.TryDecryptLine("just a normal log line", key);

        success.ShouldBeFalse();
    }

    [Fact]
    public void TryDecryptLine_Corrupted_ReturnsFalse()
    {
        var key = PodLogEncryption.DeriveLogEncryptionKey(TestMachineKey, "ticket-1");

        var (success, _) = PodLogEncryption.TryDecryptLine("SQUID_ENC|DEADBEEF", key);

        success.ShouldBeFalse();
    }

    [Fact]
    public void IsEncryptedLine_TrueForEncrypted()
    {
        PodLogEncryption.IsEncryptedLine("SQUID_ENC|ABCDEF").ShouldBeTrue();
    }

    [Fact]
    public void IsEncryptedLine_FalseForPlain()
    {
        PodLogEncryption.IsEncryptedLine("just a log line").ShouldBeFalse();
    }

    [Fact]
    public void IsEncryptedLine_FalseForNull()
    {
        PodLogEncryption.IsEncryptedLine(null).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(long.MaxValue)]
    public void EncryptDecrypt_DifferentLineNumbers_AllRoundTrip(long lineNumber)
    {
        var key = PodLogEncryption.DeriveLogEncryptionKey(TestMachineKey, "ticket-1");
        var encrypted = PodLogEncryption.EncryptLine("test line", key, lineNumber);

        var (success, plaintext) = PodLogEncryption.TryDecryptLine(encrypted, key);

        success.ShouldBeTrue();
        plaintext.ShouldBe("test line");
    }

    [Fact]
    public void TryDecryptLine_WrongKey_ReturnsFalse()
    {
        var key1 = PodLogEncryption.DeriveLogEncryptionKey(TestMachineKey, "ticket-1");
        var key2 = PodLogEncryption.DeriveLogEncryptionKey(TestMachineKey, "ticket-2");
        var encrypted = PodLogEncryption.EncryptLine("secret", key1, 1);

        var (success, _) = PodLogEncryption.TryDecryptLine(encrypted, key2);

        success.ShouldBeFalse();
    }
}
