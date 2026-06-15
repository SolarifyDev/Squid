using Autofac;
using Microsoft.Extensions.Configuration;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;

namespace Squid.IntegrationTests.Services.Security;

/// <summary>
/// Container-tier coverage of the MasterKey enforcement default (now Strict) through real DI:
/// <list type="bullet">
///   <item>a configured real key boots and round-trips a secret through the AES-256-GCM V2 envelope
///         — proves the default flip does NOT break a properly-configured deploy;</item>
///   <item>an EMPTY key (overriding the container's <see cref="SecuritySetting"/>) refuses to construct
///         the encryption service — the regression guard that FAILS if the default ever flips back to
///         Warn. The positive test alone cannot catch that, because a valid key passes
///         <c>ValidateMasterKey</c> in every mode (Off/Warn/Strict).</item>
/// </list>
///
/// <para>Both tests control <c>SQUID_MASTER_KEY_ENFORCEMENT</c> hermetically (save / set / restore) so
/// they exercise the resolved DEFAULT rather than whatever the ambient process has set. The unit suite
/// (<c>VariableEncryptionServiceMasterKeyTests</c>) proves the same default at the ctor tier; this pins
/// it end-to-end through the real <c>SquidModule</c> container.</para>
/// </summary>
public class IntegrationMasterKeyStrictDefault : TestBase
{
    public IntegrationMasterKeyStrictDefault()
        : base("MasterKeyStrictDefault", "squid_it_masterkey_strict")
    {
    }

    [Fact]
    public async Task RealContainer_ConfiguredKey_RoundTripsSecret()
    {
        // Happy path: the integration appsettings ships a real 32-byte key, so the container
        // constructs IVariableEncryptionService and a secret round-trips. (A valid key passes in
        // every enforcement mode, so this proves "a configured deploy works through real DI" — NOT
        // that the default is Strict; the negative test below is what guards the default.)
        const string secret = "round-trip-secret-7f2a-VALUE";

        var restore = SetEnforcementEnvVar(null);

        try
        {
            var (cipher, decrypted) = await Run<IVariableEncryptionService, (string Cipher, string Decrypted)>(async svc =>
            {
                var c = svc.EncryptAsync(secret, variableSetId: 1);
                var d = await svc.DecryptAsync(c, variableSetId: 1).ConfigureAwait(false);
                return (c, d);
            }).ConfigureAwait(false);

            cipher.ShouldStartWith("SQUID_ENCRYPTED_V2:",
                customMessage: "the container must boot with the configured real key and emit a V2 envelope.");
            cipher.ShouldNotContain(secret, customMessage: "ciphertext must never contain the plaintext.");
            decrypted.ShouldBe(secret,
                customMessage: "a secret encrypted under the configured key must decrypt back through real DI.");
        }
        finally { restore(); }
    }

    [Fact]
    public async Task RealContainer_StrictDefault_EmptyKey_RefusesToConstruct()
    {
        // Regression guard for the Strict default. With the enforcement env var UNSET (Strict default)
        // and the container's SecuritySetting overridden to an EMPTY MasterKey, resolving the encryption
        // service MUST throw. This fails if ReadEnforcementMode ever flips back to Warn/Off — the one
        // outcome the configured-key positive test cannot detect.
        var restore = SetEnforcementEnvVar(null);

        try
        {
            var emptyKeySetting = new SecuritySetting(new ConfigurationBuilder().Build()) { MasterKey = string.Empty };

            var ex = await Should.ThrowAsync<Exception>(() =>
                Run<IVariableEncryptionService>(_ => Task.CompletedTask,
                    builder => builder.RegisterInstance(emptyKeySetting).AsSelf().SingleInstance())).ConfigureAwait(false);

            ex.ToString().ShouldContain("MasterKey",
                customMessage:
                    "an empty MasterKey under the Strict default MUST refuse to construct the encryption service " +
                    "through real DI. If this stopped throwing, the enforcement default regressed to Warn/Off and " +
                    "secrets would silently encrypt under a recoverable key again.");
        }
        finally { restore(); }
    }

    private static Action SetEnforcementEnvVar(string value)
    {
        var name = VariableEncryptionService.EnforcementEnvVar;
        var prior = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        return () => Environment.SetEnvironmentVariable(name, prior);
    }
}
