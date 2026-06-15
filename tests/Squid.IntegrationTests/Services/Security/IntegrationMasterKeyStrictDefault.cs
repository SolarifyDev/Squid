using Squid.Core.Services.Security;

namespace Squid.IntegrationTests.Services.Security;

/// <summary>
/// Integration-tier proof that flipping the MasterKey enforcement DEFAULT to Strict
/// (env var unset) does not brick a properly-configured deployment: the real DI
/// container constructs <see cref="IVariableEncryptionService"/> and a secret
/// round-trips through the real AES-256-GCM V2 envelope.
///
/// <para>The integration <c>appsettings.json</c> ships a real random 32-byte MasterKey,
/// so construction succeeds under the now-default Strict enforcement. (An EMPTY key under
/// the Strict default throws at construction — that rejection path is unit-covered in
/// <c>VariableEncryptionServiceMasterKeyTests</c>; here we prove the happy path boots and
/// works end to end through real DI, which is what the "released master key is never null"
/// reality relies on.)</para>
/// </summary>
public class IntegrationMasterKeyStrictDefault : TestBase
{
    public IntegrationMasterKeyStrictDefault()
        : base("MasterKeyStrictDefault", "squid_it_masterkey_strict")
    {
    }

    [Fact]
    public async Task RealContainer_StrictDefault_ConfiguredKey_RoundTripsSecret()
    {
        const string secret = "round-trip-secret-7f2a-VALUE";

        var (cipher, decrypted) = await Run<IVariableEncryptionService, (string Cipher, string Decrypted)>(async svc =>
        {
            var c = svc.EncryptAsync(secret, variableSetId: 1);
            var d = await svc.DecryptAsync(c, variableSetId: 1).ConfigureAwait(false);
            return (c, d);
        }).ConfigureAwait(false);

        cipher.ShouldStartWith("SQUID_ENCRYPTED_V2:",
            customMessage: "the container must boot under the Strict default with the configured real key and emit a V2 envelope.");
        cipher.ShouldNotContain(secret,
            customMessage: "ciphertext must never contain the plaintext.");
        decrypted.ShouldBe(secret,
            customMessage: "a secret encrypted under the configured key must decrypt back through real DI under the Strict default — proves the default flip keeps a configured deploy working.");
    }
}
