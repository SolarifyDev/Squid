using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Certificates;
using Squid.Message.Enums;

namespace Squid.IntegrationTests.Services.Certificates;

/// <summary>
/// End-to-end (real Postgres + real EF + real <see cref="Squid.Core.Services.Security.IVariableEncryptionService"/>
/// with the test MasterKey) coverage of at-rest encryption for a certificate's private-key
/// material — <see cref="Certificate.Password"/> (the PFX password) AND
/// <see cref="Certificate.CertificateData"/> (the PFX/PEM blob) — through the real
/// <see cref="ICertificateDataProvider"/> seam: both raw columns carry the
/// <c>SQUID_ENCRYPTED_V2:</c> envelope and never the cleartext secret; reads decrypt both
/// transparently; pre-existing PLAINTEXT rows still read back (read-both / zero-migration); a
/// read+decrypt then unrelated same-scope SaveChanges does NOT flush plaintext back; and an
/// update that touches only metadata re-encrypts the preserved secrets.
/// </summary>
public class IntegrationCertificateSecretsAtRest : TestBase
{
    private const string Password = "pfx-pass-3b7e-VALUE";
    private const string CertData = "BASE64-CERT-BLOB-with-private-key-9f21-VALUE";

    public IntegrationCertificateSecretsAtRest()
        : base("CertificateSecretsAtRest", "squid_it_cert_secrets_atrest")
    {
    }

    [Fact]
    public async Task Create_StoresPasswordAndDataEncrypted_AndReadsBackDecrypted()
    {
        var id = await Run<ICertificateDataProvider, int>(async provider =>
        {
            var cert = NewCertificate(Password, CertData);
            await provider.AddCertificateAsync(cert).ConfigureAwait(false);
            return cert.Id;
        }).ConfigureAwait(false);

        await Run<IRepository>(async repository =>
        {
            var raw = await repository.Query<Certificate>(c => c.Id == id).FirstOrDefaultAsync().ConfigureAwait(false);

            raw.ShouldNotBeNull();
            raw.Password.ShouldStartWith("SQUID_ENCRYPTED_V2:", customMessage: "the PFX password must be a V2 envelope at rest");
            raw.CertificateData.ShouldStartWith("SQUID_ENCRYPTED_V2:", customMessage: "the cert blob (private-key material) must be a V2 envelope at rest");
            raw.Password.ShouldNotContain(Password);
            raw.CertificateData.ShouldNotContain(CertData);
        }).ConfigureAwait(false);

        await Run<ICertificateDataProvider>(async provider =>
        {
            var loaded = await provider.GetCertificateByIdAsync(id).ConfigureAwait(false);
            loaded.Password.ShouldBe(Password);
            loaded.CertificateData.ShouldBe(CertData);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task LegacyPlaintextRow_ReadsBack_NonBreaking()
    {
        var id = 0;
        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var cert = NewCertificate(Password, CertData);   // inserted raw = legacy plaintext
            await repository.InsertAsync(cert).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
            id = cert.Id;
        }).ConfigureAwait(false);

        await Run<ICertificateDataProvider>(async provider =>
        {
            var loaded = await provider.GetCertificateByIdAsync(id).ConfigureAwait(false);
            loaded.Password.ShouldBe(Password, customMessage: "read-both: legacy plaintext password reads back verbatim");
            loaded.CertificateData.ShouldBe(CertData, customMessage: "read-both: legacy plaintext cert blob reads back verbatim");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task ReadThenUnrelatedSaveInSameScope_DoesNotFlushPlaintextBack()
    {
        var id = await Run<ICertificateDataProvider, int>(async provider =>
        {
            var cert = NewCertificate(Password, CertData);
            await provider.AddCertificateAsync(cert).ConfigureAwait(false);
            return cert.Id;
        }).ConfigureAwait(false);

        await Run<ICertificateDataProvider, IUnitOfWork>(async (provider, unitOfWork) =>
        {
            var loaded = await provider.GetCertificateByIdAsync(id).ConfigureAwait(false);
            loaded.Password.ShouldBe(Password);

            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);   // would flush plaintext if reads tracked
        }).ConfigureAwait(false);

        await Run<IRepository>(async repository =>
        {
            var raw = await repository.Query<Certificate>(c => c.Id == id).FirstOrDefaultAsync().ConfigureAwait(false);
            raw.Password.ShouldStartWith("SQUID_ENCRYPTED_V2:",
                customMessage: "a read+decrypt then unrelated same-scope SaveChanges must NOT un-encrypt — reads must be AsNoTracking");
            raw.CertificateData.ShouldStartWith("SQUID_ENCRYPTED_V2:");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task MetadataUpdate_PreservesAndReEncryptsSecrets()
    {
        var id = await Run<ICertificateDataProvider, int>(async provider =>
        {
            var cert = NewCertificate(Password, CertData);
            await provider.AddCertificateAsync(cert).ConfigureAwait(false);
            return cert.Id;
        }).ConfigureAwait(false);

        // Mirror CertificateService.UpdateCertificateAsync: load (decrypted), change metadata only,
        // save (re-encrypts the preserved secrets).
        await Run<ICertificateDataProvider>(async provider =>
        {
            var loaded = await provider.GetCertificateByIdAsync(id).ConfigureAwait(false);
            loaded.Name = "renamed";
            await provider.UpdateCertificateAsync(loaded).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IRepository>(async repository =>
        {
            var raw = await repository.Query<Certificate>(c => c.Id == id).FirstOrDefaultAsync().ConfigureAwait(false);
            raw.Name.ShouldBe("renamed");
            raw.Password.ShouldStartWith("SQUID_ENCRYPTED_V2:");
            raw.CertificateData.ShouldStartWith("SQUID_ENCRYPTED_V2:");
        }).ConfigureAwait(false);

        await Run<ICertificateDataProvider>(async provider =>
        {
            var loaded = await provider.GetCertificateByIdAsync(id).ConfigureAwait(false);
            loaded.Password.ShouldBe(Password, customMessage: "the preserved password must still decrypt after a metadata update");
            loaded.CertificateData.ShouldBe(CertData);
        }).ConfigureAwait(false);
    }

    private static Certificate NewCertificate(string password, string certData) => new()
    {
        SpaceId = 1,
        Name = $"at-rest-cert-{Guid.NewGuid():N}"[..24],
        Notes = string.Empty,
        CertificateData = certData,
        Password = password,
        CertificateDataFormat = CertificateDataFormat.Pkcs12,
        HasPrivateKey = true,
        Thumbprint = Guid.NewGuid().ToString("N").ToUpperInvariant(),
        SubjectDistinguishedName = "CN=at-rest-test",
        SubjectCommonName = "at-rest-test",
        IssuerDistinguishedName = "CN=at-rest-test",
        IssuerCommonName = "at-rest-test",
        SerialNumber = "00",
        SignatureAlgorithmName = "sha256RSA",
        SubjectAlternativeNames = string.Empty,
        SelfSigned = true,
        Version = 3,
        NotBefore = DateTimeOffset.UtcNow,
        NotAfter = DateTimeOffset.UtcNow.AddYears(1)
    };
}
