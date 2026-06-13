using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;

namespace Squid.IntegrationTests.Services.Account;

/// <summary>
/// End-to-end (real Postgres + real EF + real <see cref="Squid.Core.Services.Security.IVariableEncryptionService"/>
/// with the test MasterKey) coverage of at-rest encryption for
/// <see cref="DeploymentAccount.Credentials"/> through the real
/// <see cref="IDeploymentAccountDataProvider"/> seam:
/// <list type="bullet">
///   <item>a written account's RAW DB column carries the <c>SQUID_ENCRYPTED_V2:</c>
///         envelope and never the cleartext secret;</item>
///   <item>reading it back through the provider decrypts transparently;</item>
///   <item>a pre-existing PLAINTEXT row (hand-inserted, bypassing the provider) still
///         reads back verbatim — the read-both / zero-migration guarantee;</item>
///   <item>an update re-encrypts.</item>
/// </list>
/// </summary>
public class IntegrationAccountCredentialsAtRest : TestBase
{
    private const string Secret = "top-secret-token-9c3f-VALUE";

    public IntegrationAccountCredentialsAtRest()
        : base("AccountCredentialsAtRest", "squid_it_account_creds_atrest")
    {
    }

    [Fact]
    public async Task Create_StoresCredentialsEncrypted_AndReadsBackDecrypted()
    {
        var plaintextJson = DeploymentAccountCredentialsConverter.Serialize(new TokenCredentials { Token = Secret });

        var id = await Run<IDeploymentAccountDataProvider, int>(async provider =>
        {
            var account = await provider.AddAccountAsync(NewAccount(plaintextJson)).ConfigureAwait(false);
            return account.Id;
        }).ConfigureAwait(false);

        // Raw column read via the repository directly (NOT the provider → no decrypt).
        await Run<IRepository>(async repository =>
        {
            var raw = await repository.Query<DeploymentAccount>(a => a.Id == id).FirstOrDefaultAsync().ConfigureAwait(false);

            raw.ShouldNotBeNull();
            raw.Credentials.ShouldStartWith("SQUID_ENCRYPTED_V2:",
                customMessage: "the persisted credentials column must be a V2 envelope, never cleartext");
            raw.Credentials.ShouldNotContain(Secret,
                customMessage: "the secret must not survive verbatim in the DB column");
        }).ConfigureAwait(false);

        // Through the provider → decrypted transparently.
        await Run<IDeploymentAccountDataProvider>(async provider =>
        {
            var loaded = await provider.GetAccountByIdAsync(id).ConfigureAwait(false);

            loaded.ShouldNotBeNull();
            loaded.Credentials.ShouldBe(plaintextJson, customMessage: "the provider must decrypt on read");

            var creds = DeploymentAccountCredentialsConverter.Deserialize(AccountType.Token, loaded.Credentials) as TokenCredentials;
            creds.ShouldNotBeNull();
            creds.Token.ShouldBe(Secret);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task LegacyPlaintextRow_ReadsBack_NonBreaking()
    {
        // Hand-insert a row with PLAINTEXT credentials (bypassing the encrypting provider)
        // to simulate data written before this feature shipped.
        var plaintextJson = DeploymentAccountCredentialsConverter.Serialize(new TokenCredentials { Token = Secret });

        var id = 0;
        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var account = NewAccount(plaintextJson);
            await repository.InsertAsync(account).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
            id = account.Id;
        }).ConfigureAwait(false);

        await Run<IDeploymentAccountDataProvider>(async provider =>
        {
            var loaded = await provider.GetAccountByIdAsync(id).ConfigureAwait(false);

            loaded.ShouldNotBeNull();
            loaded.Credentials.ShouldBe(plaintextJson,
                customMessage: "read-both: a pre-existing plaintext row must read back verbatim with no error");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Update_ReEncryptsCredentials()
    {
        const string newSecret = "rotated-secret-7b21-VALUE";
        var originalJson = DeploymentAccountCredentialsConverter.Serialize(new TokenCredentials { Token = Secret });
        var rotatedJson = DeploymentAccountCredentialsConverter.Serialize(new TokenCredentials { Token = newSecret });

        var id = await Run<IDeploymentAccountDataProvider, int>(async provider =>
        {
            var account = await provider.AddAccountAsync(NewAccount(originalJson)).ConfigureAwait(false);
            return account.Id;
        }).ConfigureAwait(false);

        await Run<IDeploymentAccountDataProvider>(async provider =>
        {
            var loaded = await provider.GetAccountByIdAsync(id).ConfigureAwait(false);   // decrypted
            loaded.Credentials = rotatedJson;                                            // plaintext rotation
            await provider.UpdateAccountAsync(loaded).ConfigureAwait(false);             // re-encrypts
        }).ConfigureAwait(false);

        await Run<IRepository>(async repository =>
        {
            var raw = await repository.Query<DeploymentAccount>(a => a.Id == id).FirstOrDefaultAsync().ConfigureAwait(false);

            raw.Credentials.ShouldStartWith("SQUID_ENCRYPTED_V2:");
            raw.Credentials.ShouldNotContain(newSecret);
        }).ConfigureAwait(false);

        await Run<IDeploymentAccountDataProvider>(async provider =>
        {
            var loaded = await provider.GetAccountByIdAsync(id).ConfigureAwait(false);
            loaded.Credentials.ShouldBe(rotatedJson, customMessage: "the rotated secret must decrypt back after update");
        }).ConfigureAwait(false);
    }

    private static DeploymentAccount NewAccount(string credentialsJson) => new()
    {
        SpaceId = 1,
        Name = $"at-rest-{Guid.NewGuid():N}"[..20],
        Slug = $"account-{Guid.NewGuid():N}",
        AccountType = AccountType.Token,
        Credentials = credentialsJson,
        EnvironmentIds = null
    };
}
