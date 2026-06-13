using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Account;

/// <summary>
/// Pins the crypto contract that <see cref="DeploymentAccountDataProvider"/> relies on to
/// protect <see cref="Squid.Core.Persistence.Entities.Deployments.DeploymentAccount.Credentials"/>
/// at rest: every account type's serialized credentials JSON round-trips through the
/// <c>SQUID_ENCRYPTED_V2:</c> envelope with the secret intact, a pre-existing PLAINTEXT
/// value passes straight through on read (the read-both / non-breaking guarantee), and a
/// tampered ciphertext is rejected by the GCM tag.
///
/// <para>The provider wraps the WHOLE serialized blob, so this exercises the realistic
/// payloads — large GCP JSON keys, multi-line SSH private keys, cloud secrets.</para>
/// </summary>
public sealed class DeploymentAccountCredentialsEncryptionTests
{
    [Theory]
    [MemberData(nameof(EveryAccountTypeWithSecrets))]
    public async Task SerializedCredentials_RoundTripThroughEnvelope_SecretIntact(AccountType accountType, object credentials)
    {
        var encryption = MakeEncryptionService();
        var plaintextJson = DeploymentAccountCredentialsConverter.Serialize(credentials);

        var stored = encryption.EncryptAsync(plaintextJson, variableSetId: 0);

        stored.ShouldStartWith("SQUID_ENCRYPTED_V2:", customMessage: "the at-rest column value must be a V2 envelope, never cleartext");
        encryption.IsValidEncryptedValue(stored).ShouldBeTrue();
        stored.ShouldNotContain("super-secret", customMessage: "no secret material may survive verbatim in the envelope");

        var roundTripped = await encryption.DecryptAsync(stored, variableSetId: 0).ConfigureAwait(false);

        roundTripped.ShouldBe(plaintextJson);
        // And the decrypted JSON still deserializes back to credentials with the secret present.
        DeploymentAccountCredentialsConverter.Serialize(
            DeploymentAccountCredentialsConverter.Deserialize(accountType, roundTripped)).ShouldBe(plaintextJson);
    }

    [Fact]
    public async Task LegacyPlaintextValue_ReadsBackVerbatim_NonBreaking()
    {
        // A row written before this feature has no envelope prefix. The read path must
        // return it untouched so existing accounts keep working with zero migration.
        var encryption = MakeEncryptionService();
        var legacyPlaintext = DeploymentAccountCredentialsConverter.Serialize(new TokenCredentials { Token = "super-secret-token" });

        encryption.IsValidEncryptedValue(legacyPlaintext).ShouldBeFalse(
            customMessage: "plaintext JSON must NOT be mistaken for an envelope (it has no SQUID_ENCRYPTED prefix)");

        (await encryption.DecryptAsync(legacyPlaintext, variableSetId: 0).ConfigureAwait(false))
            .ShouldBe(legacyPlaintext, customMessage: "read-both: an unprefixed value is returned verbatim");
    }

    [Fact]
    public async Task AlreadyEncryptedValue_IsDetected_SoTheProviderNeverDoubleWraps()
    {
        // The provider's encrypt-on-write is guarded by IsValidEncryptedValue so a re-save
        // never wraps an envelope inside another envelope.
        var encryption = MakeEncryptionService();
        var once = encryption.EncryptAsync("{\"token\":\"super-secret\"}", variableSetId: 0);

        encryption.IsValidEncryptedValue(once).ShouldBeTrue();

        // Decrypting the once-encrypted value yields the original (not a second envelope).
        (await encryption.DecryptAsync(once, variableSetId: 0).ConfigureAwait(false))
            .ShouldBe("{\"token\":\"super-secret\"}");
    }

    [Fact]
    public async Task TamperedCiphertext_IsRejected()
    {
        var encryption = MakeEncryptionService();
        var stored = encryption.EncryptAsync("{\"secretKey\":\"super-secret\"}", variableSetId: 0);

        // Flip a byte in the base64 body — GCM tag authentication must reject it.
        var body = stored["SQUID_ENCRYPTED_V2:".Length..].ToCharArray();
        body[^2] = body[^2] == 'A' ? 'B' : 'A';
        var tampered = "SQUID_ENCRYPTED_V2:" + new string(body);

        await Should.ThrowAsync<Exception>(() => encryption.DecryptAsync(tampered, variableSetId: 0));
    }

    public static IEnumerable<object[]> EveryAccountTypeWithSecrets() => new List<object[]>
    {
        new object[] { AccountType.Token, new TokenCredentials { Token = "super-secret-token" } },
        new object[] { AccountType.UsernamePassword, new UsernamePasswordCredentials { Username = "svc", Password = "super-secret-pw" } },
        new object[] { AccountType.ClientCertificate, new ClientCertificateCredentials { ClientCertificateData = "cert", ClientCertificateKeyData = "super-secret-key" } },
        new object[] { AccountType.AmazonWebServicesAccount, new AwsCredentials { AccessKey = "AKIA", SecretKey = "super-secret-aws" } },
        new object[] { AccountType.AmazonWebServicesRoleAccount, new AwsRoleCredentials { AccessKey = "AKIA", SecretKey = "super-secret-aws", RoleArn = "arn:aws:iam::1:role/x" } },
        new object[] { AccountType.SshKeyPair, new SshKeyPairCredentials { Username = "deploy", PrivateKeyFile = "-----BEGIN KEY-----\nsuper-secret-line\n-----END KEY-----", PrivateKeyPassphrase = "super-secret-pass" } },
        new object[] { AccountType.AzureServicePrincipal, new AzureServicePrincipalCredentials { SubscriptionNumber = "sub", ClientId = "cid", TenantId = "tid", Key = "super-secret-sp" } },
        new object[] { AccountType.AzureOidc, new AzureOidcCredentials { SubscriptionNumber = "sub", ClientId = "cid", TenantId = "tid", Jwt = "super-secret-jwt" } },
        new object[] { AccountType.GoogleCloudAccount, new GcpCredentials { JsonKey = "{\"type\":\"service_account\",\"private_key\":\"super-secret-gcp\"}" } },
        new object[] { AccountType.AmazonWebServicesOidcAccount, new AwsOidcCredentials { RoleArn = "arn:aws:iam::1:role/x", WebIdentityToken = "super-secret-token" } },
        new object[] { AccountType.OpenClawGateway, new OpenClawGatewayCredentials { GatewayToken = "super-secret-gw", HooksToken = "super-secret-hooks" } },
    };

    private static IVariableEncryptionService MakeEncryptionService()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(0x40 + i);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Security:VariableEncryption:MasterKey"] = Convert.ToBase64String(key)
            })
            .Build();

        return new VariableEncryptionService(new SecuritySetting(configuration));
    }
}
