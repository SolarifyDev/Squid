using Shouldly;
using Squid.Tentacle.Security;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Security;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class MachineIdKeyEncryptorTests
{
    [Fact]
    public void ProtectThenUnprotect_RoundTripsExactly()
    {
        var enc = new MachineIdKeyEncryptor("test-machine-id-abc123");

        var protectedText = enc.Protect("hunter2-is-my-password");
        var recovered = enc.Unprotect(protectedText);

        recovered.ShouldBe("hunter2-is-my-password");
    }

    [Fact]
    public void Protect_DifferentInvocations_ProduceDifferentCipherText()
    {
        var enc = new MachineIdKeyEncryptor("test-machine-id");

        var a = enc.Protect("same-input");
        var b = enc.Protect("same-input");

        a.ShouldNotBe(b, "a random nonce per encryption must make the cipher non-deterministic");
    }

    [Fact]
    public void Unprotect_WithDifferentMachineId_Fails()
    {
        var host1 = new MachineIdKeyEncryptor("machine-a");
        var host2 = new MachineIdKeyEncryptor("machine-b");

        var protectedText = host1.Protect("secret");

        Should.Throw<System.Security.Cryptography.CryptographicException>(() => host2.Unprotect(protectedText));
    }

    [Fact]
    public void Unprotect_MissingVersionPrefix_Throws()
    {
        var enc = new MachineIdKeyEncryptor("m");

        Should.Throw<FormatException>(() => enc.Unprotect("some-text-without-prefix"));
    }

    [Fact]
    public void Unprotect_TamperedCipherText_Fails()
    {
        var enc = new MachineIdKeyEncryptor("m");
        var protectedText = enc.Protect("hello");

        // Flip a single character in the base64 body.
        var prefixLength = "v1:".Length;
        var tampered = "v1:" + protectedText[prefixLength..][..^1] + (protectedText[^1] == 'A' ? 'B' : 'A');

        Should.Throw<System.Security.Cryptography.CryptographicException>(() => enc.Unprotect(tampered));
    }

    [Fact]
    public void Protect_UnicodePayload_RoundTrips()
    {
        var enc = new MachineIdKeyEncryptor("m");

        var recovered = enc.Unprotect(enc.Protect("password — 你好 🔒"));

        recovered.ShouldBe("password — 你好 🔒");
    }

    [Fact]
    public void Ctor_EmptyMachineId_Throws()
    {
        Should.Throw<ArgumentException>(() => new MachineIdKeyEncryptor(""));
    }
}
