using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Squid.Core.Services.Deployments.Certificates;
using Squid.Message.Commands.Deployments.Certificate;
using Squid.Message.Enums;
using Squid.Message.Requests.Deployments.Certificate;

namespace Squid.IntegrationTests.Deployments.Certificates;

public class IntegrationCertificateCrud : CertificateFixtureBase
{
    // === Test Certificate Generators ===

    private static (byte[] pfxBytes, string password, X509Certificate2 cert) GenerateSelfSignedPfx(
        string cn = "Integration Test Cert",
        int validDays = 365,
        string password = "test-password")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(validDays));

        var pfxBytes = cert.Export(X509ContentType.Pfx, password);

        return (pfxBytes, password, cert);
    }

    private static (byte[] pfxBytes, string password, X509Certificate2 cert) GenerateSelfSignedPfxWithSan(
        string cn = "SAN Test Cert",
        string[] dnsNames = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        if (dnsNames != null)
        {
            var sanBuilder = new SubjectAlternativeNameBuilder();

            foreach (var dns in dnsNames)
                sanBuilder.AddDnsName(dns);

            request.CertificateExtensions.Add(sanBuilder.Build());
        }

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));

        var password = "test-password";
        var pfxBytes = cert.Export(X509ContentType.Pfx, password);

        return (pfxBytes, password, cert);
    }

    private static byte[] GenerateDerBytes(string cn = "DER Integration Test")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));

        return cert.Export(X509ContentType.Cert);
    }

    private static byte[] GeneratePemBytes(string cn = "PEM Integration Test")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));

        var pem = cert.ExportCertificatePem();
        return Encoding.UTF8.GetBytes(pem);
    }

    // ========== Create — PFX Format ==========

    [Fact]
    public async Task Create_Pfx_PersistsAndReturnsAllMetadata()
    {
        await Run<ICertificateService>(async service =>
        {
            var (pfxBytes, password, cert) = GenerateSelfSignedPfx(cn: "PFX Persist Test");

            var @event = await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "PFX Persist Test",
                Notes = "Integration test PFX",
                CertificateData = Convert.ToBase64String(pfxBytes),
                Password = password,
                SpaceId = 1,
                EnvironmentIds = new List<int> { 1, 2 }
            }, CancellationToken.None).ConfigureAwait(false);

            var dto = @event.Data;
            dto.Id.ShouldBeGreaterThan(0);
            dto.Name.ShouldBe("PFX Persist Test");
            dto.Notes.ShouldBe("Integration test PFX");
            dto.CertificateDataFormat.ShouldBe(CertificateDataFormat.Pkcs12);
            dto.Thumbprint.ShouldBe(cert.Thumbprint);
            dto.SubjectCommonName.ShouldBe("PFX Persist Test");
            dto.IssuerCommonName.ShouldBe("PFX Persist Test");
            dto.SelfSigned.ShouldBeTrue();
            dto.HasPrivateKey.ShouldBeTrue();
            dto.IsExpired.ShouldBeFalse();
            dto.Version.ShouldBe(3);
            dto.SerialNumber.ShouldNotBeNullOrEmpty();
            dto.SignatureAlgorithmName.ShouldNotBeNullOrEmpty();
            dto.PasswordHasValue.ShouldBeTrue();
            dto.EnvironmentIds.ShouldBe(new List<int> { 1, 2 });
            dto.Archived.ShouldBeNull();
            dto.ReplacedBy.ShouldBeNull();
        }).ConfigureAwait(false);
    }

    // ========== Create — PEM Format ==========

    [Fact]
    public async Task Create_Pem_PersistsCorrectFormat()
    {
        await Run<ICertificateService>(async service =>
        {
            var pemBytes = GeneratePemBytes(cn: "PEM Persist Test");

            var @event = await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "PEM Certificate",
                CertificateData = Convert.ToBase64String(pemBytes),
                SpaceId = 1
            }, CancellationToken.None).ConfigureAwait(false);

            @event.Data.CertificateDataFormat.ShouldBe(CertificateDataFormat.Pem);
            @event.Data.SubjectCommonName.ShouldBe("PEM Persist Test");
            @event.Data.HasPrivateKey.ShouldBeFalse();
            @event.Data.PasswordHasValue.ShouldBeFalse();
        }).ConfigureAwait(false);
    }

    // ========== Create — DER Format ==========

    [Fact]
    public async Task Create_Der_PersistsCorrectFormat()
    {
        await Run<ICertificateService>(async service =>
        {
            var derBytes = GenerateDerBytes(cn: "DER Persist Test");

            var @event = await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "DER Certificate",
                CertificateData = Convert.ToBase64String(derBytes),
                SpaceId = 1
            }, CancellationToken.None).ConfigureAwait(false);

            @event.Data.CertificateDataFormat.ShouldBe(CertificateDataFormat.Der);
            @event.Data.SubjectCommonName.ShouldBe("DER Persist Test");
            @event.Data.HasPrivateKey.ShouldBeFalse();
        }).ConfigureAwait(false);
    }

    // ========== Create — SAN Extension ==========

    [Fact]
    public async Task Create_WithSan_PersistsSubjectAlternativeNames()
    {
        await Run<ICertificateService>(async service =>
        {
            var (pfxBytes, password, _) = GenerateSelfSignedPfxWithSan(
                cn: "SAN Test",
                dnsNames: new[] { "example.com", "*.example.com" });

            var @event = await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "SAN Certificate",
                CertificateData = Convert.ToBase64String(pfxBytes),
                Password = password,
                SpaceId = 1
            }, CancellationToken.None).ConfigureAwait(false);

            @event.Data.SubjectAlternativeNames.ShouldNotBeNull();
            @event.Data.SubjectAlternativeNames.ShouldNotBeEmpty();
        }).ConfigureAwait(false);
    }

    // ========== Create then Read Back ==========

    [Fact]
    public async Task Create_ThenGetById_ReturnsPersistedEntity()
    {
        await Run<ICertificateService, ICertificateDataProvider>(async (service, provider) =>
        {
            var (pfxBytes, password, cert) = GenerateSelfSignedPfx(cn: "Read Back Test");

            var @event = await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "Read Back Test",
                Notes = "Verify persistence",
                CertificateData = Convert.ToBase64String(pfxBytes),
                Password = password,
                SpaceId = 1,
                EnvironmentIds = new List<int> { 3 }
            }, CancellationToken.None).ConfigureAwait(false);

            var entity = await provider.GetCertificateByIdAsync(@event.Data.Id, CancellationToken.None).ConfigureAwait(false);

            entity.ShouldNotBeNull();
            entity.Name.ShouldBe("Read Back Test");
            entity.Thumbprint.ShouldBe(cert.Thumbprint);
            entity.CertificateData.ShouldNotBeNullOrEmpty();
            entity.EnvironmentIds.ShouldBe("3");
        }).ConfigureAwait(false);
    }

    // ========== Update ==========

    [Fact]
    public async Task Update_ChangesNameNotesAndEnvironmentIds()
    {
        await Run<ICertificateService>(async service =>
        {
            var (pfxBytes, password, cert) = GenerateSelfSignedPfx();

            var created = await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "Original Name",
                Notes = "Original Notes",
                CertificateData = Convert.ToBase64String(pfxBytes),
                Password = password,
                SpaceId = 1,
                EnvironmentIds = new List<int> { 1 }
            }, CancellationToken.None).ConfigureAwait(false);

            var updated = await service.UpdateCertificateAsync(new UpdateCertificateCommand
            {
                Id = created.Data.Id,
                Name = "Updated Name",
                Notes = "Updated Notes",
                EnvironmentIds = new List<int> { 2, 3 }
            }, CancellationToken.None).ConfigureAwait(false);

            updated.Data.Name.ShouldBe("Updated Name");
            updated.Data.Notes.ShouldBe("Updated Notes");
            updated.Data.EnvironmentIds.ShouldBe(new List<int> { 2, 3 });

            // X.509 metadata preserved
            updated.Data.Thumbprint.ShouldBe(cert.Thumbprint);
            updated.Data.SubjectCommonName.ShouldBe(created.Data.SubjectCommonName);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Update_NotFound_Throws()
    {
        await Run<ICertificateService>(async service =>
        {
            await Should.ThrowAsync<Exception>(async () =>
                await service.UpdateCertificateAsync(new UpdateCertificateCommand
                {
                    Id = 99999,
                    Name = "Nonexistent"
                }, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    // ========== Replace ==========

    [Fact]
    public async Task Replace_ArchivesOriginalAndCreatesNewEntity()
    {
        await Run<ICertificateService, ICertificateDataProvider>(async (service, provider) =>
        {
            var (pfxBytes1, password1, cert1) = GenerateSelfSignedPfx(cn: "Original Cert");

            var created = await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "Replaceable Cert",
                Notes = "Will be replaced",
                CertificateData = Convert.ToBase64String(pfxBytes1),
                Password = password1,
                SpaceId = 1,
                EnvironmentIds = new List<int> { 1, 2 }
            }, CancellationToken.None).ConfigureAwait(false);

            var originalId = created.Data.Id;

            var (pfxBytes2, password2, cert2) = GenerateSelfSignedPfx(cn: "Replacement Cert");

            var replaced = await service.ReplaceCertificateAsync(new ReplaceCertificateCommand
            {
                Id = originalId,
                CertificateData = Convert.ToBase64String(pfxBytes2),
                Password = password2
            }, CancellationToken.None).ConfigureAwait(false);

            // New certificate has new metadata
            replaced.Data.Thumbprint.ShouldBe(cert2.Thumbprint);
            replaced.Data.SubjectCommonName.ShouldBe("Replacement Cert");
            replaced.Data.Id.ShouldNotBe(originalId);

            // New certificate inherits Name, Notes, EnvironmentIds
            replaced.Data.Name.ShouldBe("Replaceable Cert");
            replaced.Data.EnvironmentIds.ShouldBe(new List<int> { 1, 2 });

            // Original is archived
            var original = await provider.GetCertificateByIdAsync(originalId, CancellationToken.None).ConfigureAwait(false);
            original.Archived.ShouldNotBeNull();
            original.ReplacedBy.ShouldBe(replaced.Data.Id);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Replace_NotFound_Throws()
    {
        await Run<ICertificateService>(async service =>
        {
            var (pfxBytes, password, _) = GenerateSelfSignedPfx();

            await Should.ThrowAsync<Exception>(async () =>
                await service.ReplaceCertificateAsync(new ReplaceCertificateCommand
                {
                    Id = 99999,
                    CertificateData = Convert.ToBase64String(pfxBytes),
                    Password = password
                }, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    // ========== Delete ==========

    [Fact]
    public async Task Delete_RemovesEntities()
    {
        await Run<ICertificateService, ICertificateDataProvider>(async (service, provider) =>
        {
            var (pfxBytes, password, _) = GenerateSelfSignedPfx();

            var created = await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "Deletable Cert",
                CertificateData = Convert.ToBase64String(pfxBytes),
                Password = password,
                SpaceId = 1
            }, CancellationToken.None).ConfigureAwait(false);

            var @event = await service.DeleteCertificatesAsync(new DeleteCertificatesCommand
            {
                Ids = new List<int> { created.Data.Id }
            }, CancellationToken.None).ConfigureAwait(false);

            @event.Data.FailIds.ShouldBeEmpty();

            var deleted = await provider.GetCertificateByIdAsync(created.Data.Id, CancellationToken.None).ConfigureAwait(false);
            deleted.ShouldBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Delete_PartiallyFound_ReturnsFailIds()
    {
        await Run<ICertificateService>(async service =>
        {
            var (pfxBytes, password, _) = GenerateSelfSignedPfx();

            var created = await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "Partial Delete",
                CertificateData = Convert.ToBase64String(pfxBytes),
                Password = password,
                SpaceId = 1
            }, CancellationToken.None).ConfigureAwait(false);

            var @event = await service.DeleteCertificatesAsync(new DeleteCertificatesCommand
            {
                Ids = new List<int> { created.Data.Id, 99998, 99999 }
            }, CancellationToken.None).ConfigureAwait(false);

            @event.Data.FailIds.ShouldContain(99998);
            @event.Data.FailIds.ShouldContain(99999);
            @event.Data.FailIds.Count.ShouldBe(2);
        }).ConfigureAwait(false);
    }

    // ========== List / Pagination ==========

    [Fact]
    public async Task GetCertificates_ReturnsAllCreated()
    {
        await Run<ICertificateService>(async service =>
        {
            var (pfx1, pwd1, _) = GenerateSelfSignedPfx(cn: "List Cert 1");
            var (pfx2, pwd2, _) = GenerateSelfSignedPfx(cn: "List Cert 2");
            var (pfx3, pwd3, _) = GenerateSelfSignedPfx(cn: "List Cert 3");

            await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "List Cert 1",
                CertificateData = Convert.ToBase64String(pfx1),
                Password = pwd1,
                SpaceId = 1
            }, CancellationToken.None).ConfigureAwait(false);

            await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "List Cert 2",
                CertificateData = Convert.ToBase64String(pfx2),
                Password = pwd2,
                SpaceId = 1
            }, CancellationToken.None).ConfigureAwait(false);

            await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "List Cert 3",
                CertificateData = Convert.ToBase64String(pfx3),
                Password = pwd3,
                SpaceId = 1
            }, CancellationToken.None).ConfigureAwait(false);

            var response = await service.GetCertificatesAsync(new GetCertificatesRequest
            {
                PageIndex = 1,
                PageSize = 10
            }, CancellationToken.None).ConfigureAwait(false);

            response.Data.Count.ShouldBe(3);
            response.Data.Certificates.Count.ShouldBe(3);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetCertificates_Pagination_ReturnsCorrectPage()
    {
        await Run<ICertificateService>(async service =>
        {
            for (var i = 1; i <= 5; i++)
            {
                var (pfx, pwd, _) = GenerateSelfSignedPfx(cn: $"Page Cert {i}");

                await service.CreateCertificateAsync(new CreateCertificateCommand
                {
                    Name = $"Page Cert {i}",
                    CertificateData = Convert.ToBase64String(pfx),
                    Password = pwd,
                    SpaceId = 1
                }, CancellationToken.None).ConfigureAwait(false);
            }

            var page1 = await service.GetCertificatesAsync(new GetCertificatesRequest
            {
                PageIndex = 1,
                PageSize = 2
            }, CancellationToken.None).ConfigureAwait(false);

            var page2 = await service.GetCertificatesAsync(new GetCertificatesRequest
            {
                PageIndex = 2,
                PageSize = 2
            }, CancellationToken.None).ConfigureAwait(false);

            page1.Data.Count.ShouldBe(5);
            page1.Data.Certificates.Count.ShouldBe(2);
            page2.Data.Certificates.Count.ShouldBe(2);
        }).ConfigureAwait(false);
    }

    // ========== Full Lifecycle ==========

    [Fact]
    public async Task FullLifecycle_CreateUpdateReplaceDelete()
    {
        await Run<ICertificateService, ICertificateDataProvider>(async (service, provider) =>
        {
            // 1. Create
            var (pfxBytes1, password1, cert1) = GenerateSelfSignedPfx(cn: "Lifecycle Test");

            var created = await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "Lifecycle Cert",
                Notes = "Step 1",
                CertificateData = Convert.ToBase64String(pfxBytes1),
                Password = password1,
                SpaceId = 1,
                EnvironmentIds = new List<int> { 1 }
            }, CancellationToken.None).ConfigureAwait(false);

            var originalId = created.Data.Id;
            created.Data.Thumbprint.ShouldBe(cert1.Thumbprint);

            // 2. Update
            var updated = await service.UpdateCertificateAsync(new UpdateCertificateCommand
            {
                Id = originalId,
                Name = "Lifecycle Cert Updated",
                Notes = "Step 2",
                EnvironmentIds = new List<int> { 1, 2 }
            }, CancellationToken.None).ConfigureAwait(false);

            updated.Data.Name.ShouldBe("Lifecycle Cert Updated");
            updated.Data.Thumbprint.ShouldBe(cert1.Thumbprint);

            // 3. Replace
            var (pfxBytes2, password2, cert2) = GenerateSelfSignedPfx(cn: "Lifecycle Replacement");

            var replaced = await service.ReplaceCertificateAsync(new ReplaceCertificateCommand
            {
                Id = originalId,
                CertificateData = Convert.ToBase64String(pfxBytes2),
                Password = password2
            }, CancellationToken.None).ConfigureAwait(false);

            var newId = replaced.Data.Id;
            replaced.Data.Thumbprint.ShouldBe(cert2.Thumbprint);
            replaced.Data.Name.ShouldBe("Lifecycle Cert Updated");

            // Verify original is archived
            var originalEntity = await provider.GetCertificateByIdAsync(originalId, CancellationToken.None).ConfigureAwait(false);
            originalEntity.Archived.ShouldNotBeNull();
            originalEntity.ReplacedBy.ShouldBe(newId);

            // 4. Delete both
            var deleted = await service.DeleteCertificatesAsync(new DeleteCertificatesCommand
            {
                Ids = new List<int> { originalId, newId }
            }, CancellationToken.None).ConfigureAwait(false);

            deleted.Data.FailIds.ShouldBeEmpty();

            // Verify both gone
            var list = await service.GetCertificatesAsync(new GetCertificatesRequest(), CancellationToken.None).ConfigureAwait(false);
            list.Data.Count.ShouldBe(0);
        }).ConfigureAwait(false);
    }

    // ========== Mediator (Handler → Service → DB) ==========

    [Fact]
    public async Task Mediator_CreateCertificate_ReturnsResponse()
    {
        await Run<IMediator>(async mediator =>
        {
            var (pfxBytes, password, cert) = GenerateSelfSignedPfx(cn: "Mediator Test");

            var response = await mediator.SendAsync<CreateCertificateCommand, CreateCertificateResponse>(
                new CreateCertificateCommand
                {
                    Name = "Mediator Cert",
                    CertificateData = Convert.ToBase64String(pfxBytes),
                    Password = password,
                    SpaceId = 1
                }).ConfigureAwait(false);

            response.Data.Certificate.ShouldNotBeNull();
            response.Data.Certificate.Thumbprint.ShouldBe(cert.Thumbprint);
            response.Data.Certificate.Name.ShouldBe("Mediator Cert");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Mediator_UpdateCertificate_ReturnsResponse()
    {
        await Run<IMediator>(async mediator =>
        {
            var (pfxBytes, password, _) = GenerateSelfSignedPfx();

            var createResponse = await mediator.SendAsync<CreateCertificateCommand, CreateCertificateResponse>(
                new CreateCertificateCommand
                {
                    Name = "Before Update",
                    CertificateData = Convert.ToBase64String(pfxBytes),
                    Password = password,
                    SpaceId = 1
                }).ConfigureAwait(false);

            var updateResponse = await mediator.SendAsync<UpdateCertificateCommand, UpdateCertificateResponse>(
                new UpdateCertificateCommand
                {
                    Id = createResponse.Data.Certificate.Id,
                    Name = "After Update",
                    Notes = "Updated via mediator"
                }).ConfigureAwait(false);

            updateResponse.Data.Certificate.Name.ShouldBe("After Update");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Mediator_ReplaceCertificate_ReturnsResponse()
    {
        await Run<IMediator>(async mediator =>
        {
            var (pfxBytes1, password1, _) = GenerateSelfSignedPfx(cn: "Mediator Original");

            var createResponse = await mediator.SendAsync<CreateCertificateCommand, CreateCertificateResponse>(
                new CreateCertificateCommand
                {
                    Name = "Replace Via Mediator",
                    CertificateData = Convert.ToBase64String(pfxBytes1),
                    Password = password1,
                    SpaceId = 1
                }).ConfigureAwait(false);

            var (pfxBytes2, password2, cert2) = GenerateSelfSignedPfx(cn: "Mediator Replacement");

            var replaceResponse = await mediator.SendAsync<ReplaceCertificateCommand, ReplaceCertificateResponse>(
                new ReplaceCertificateCommand
                {
                    Id = createResponse.Data.Certificate.Id,
                    CertificateData = Convert.ToBase64String(pfxBytes2),
                    Password = password2
                }).ConfigureAwait(false);

            replaceResponse.Data.Certificate.Thumbprint.ShouldBe(cert2.Thumbprint);
            replaceResponse.Data.Certificate.Name.ShouldBe("Replace Via Mediator");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Mediator_DeleteCertificates_ReturnsResponse()
    {
        await Run<IMediator>(async mediator =>
        {
            var (pfxBytes, password, _) = GenerateSelfSignedPfx();

            var createResponse = await mediator.SendAsync<CreateCertificateCommand, CreateCertificateResponse>(
                new CreateCertificateCommand
                {
                    Name = "Delete Via Mediator",
                    CertificateData = Convert.ToBase64String(pfxBytes),
                    Password = password,
                    SpaceId = 1
                }).ConfigureAwait(false);

            var deleteResponse = await mediator.SendAsync<DeleteCertificatesCommand, DeleteCertificatesResponse>(
                new DeleteCertificatesCommand
                {
                    Ids = new List<int> { createResponse.Data.Certificate.Id }
                }).ConfigureAwait(false);

            deleteResponse.Data.FailIds.ShouldBeEmpty();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Mediator_GetCertificates_ReturnsPaginatedList()
    {
        await Run<IMediator>(async mediator =>
        {
            var (pfxBytes, password, _) = GenerateSelfSignedPfx();

            await mediator.SendAsync<CreateCertificateCommand, CreateCertificateResponse>(
                new CreateCertificateCommand
                {
                    Name = "List Via Mediator",
                    CertificateData = Convert.ToBase64String(pfxBytes),
                    Password = password,
                    SpaceId = 1
                }).ConfigureAwait(false);

            var response = await mediator.RequestAsync<GetCertificatesRequest, GetCertificatesResponse>(
                new GetCertificatesRequest { PageIndex = 1, PageSize = 10 }).ConfigureAwait(false);

            response.Data.Count.ShouldBeGreaterThanOrEqualTo(1);
            response.Data.Certificates.ShouldContain(c => c.Name == "List Via Mediator");
        }).ConfigureAwait(false);
    }

    // ========== Create — Generate Self-Signed (no CertificateData) ==========

    [Theory]
    [InlineData(CertificateKeyType.RSA2048)]
    [InlineData(CertificateKeyType.RSA4096)]
    [InlineData(CertificateKeyType.NistP256)]
    [InlineData(CertificateKeyType.NistP384)]
    [InlineData(CertificateKeyType.NistP521)]
    public async Task Generate_AllKeyTypes_PersistsSuccessfully(CertificateKeyType keyType)
    {
        await Run<ICertificateService>(async service =>
        {
            var @event = await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = $"KeyType-{keyType}",
                ValidityDays = 365,
                KeyType = keyType,
                SpaceId = 1
            }, CancellationToken.None).ConfigureAwait(false);

            var dto = @event.Data;
            dto.Id.ShouldBeGreaterThan(0);
            dto.SubjectCommonName.ShouldBe($"KeyType-{keyType}");
            dto.SelfSigned.ShouldBeTrue();
            dto.HasPrivateKey.ShouldBeTrue();
            dto.CertificateDataFormat.ShouldBe(CertificateDataFormat.Pkcs12);
            dto.Thumbprint.ShouldNotBeNullOrEmpty();
            dto.PasswordHasValue.ShouldBeTrue();
            dto.IsExpired.ShouldBeFalse();
            dto.Version.ShouldBe(3);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Generate_NameAndCommonNameAreSeparate()
    {
        await Run<ICertificateService>(async service =>
        {
            var @event = await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "Production TLS",
                CommonName = "prod.example.com",
                ValidityDays = 365,
                KeyType = CertificateKeyType.RSA2048,
                SpaceId = 1
            }, CancellationToken.None).ConfigureAwait(false);

            @event.Data.Name.ShouldBe("Production TLS");
            @event.Data.SubjectCommonName.ShouldBe("prod.example.com");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Generate_WithSans_PersistsSubjectAlternativeNames()
    {
        await Run<ICertificateService>(async service =>
        {
            var @event = await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "SAN Gen Test",
                ValidityDays = 365,
                KeyType = CertificateKeyType.NistP256,
                SubjectAlternativeNames = new List<string> { "app.example.com", "*.example.com" },
                SpaceId = 1
            }, CancellationToken.None).ConfigureAwait(false);

            @event.Data.SubjectAlternativeNames.ShouldNotBeEmpty();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Generate_CustomValidity_RespectsNotAfter()
    {
        await Run<ICertificateService>(async service =>
        {
            var @event = await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "Short Lived",
                ValidityDays = 30,
                KeyType = CertificateKeyType.RSA2048,
                SpaceId = 1
            }, CancellationToken.None).ConfigureAwait(false);

            var expectedNotAfter = DateTimeOffset.UtcNow.AddDays(30);

            @event.Data.NotAfter.ShouldBeGreaterThan(expectedNotAfter.AddHours(-1));
            @event.Data.NotAfter.ShouldBeLessThan(expectedNotAfter.AddHours(1));
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Generate_WithEnvironmentIds_PersistsScoping()
    {
        await Run<ICertificateService>(async service =>
        {
            var @event = await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "Scoped Gen Cert",
                ValidityDays = 365,
                KeyType = CertificateKeyType.NistP384,
                EnvironmentIds = new List<int> { 1, 2, 3 },
                SpaceId = 1
            }, CancellationToken.None).ConfigureAwait(false);

            @event.Data.EnvironmentIds.ShouldBe(new List<int> { 1, 2, 3 });
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Generate_ThenReadBack_DataIsPersisted()
    {
        await Run<ICertificateService, ICertificateDataProvider>(async (service, provider) =>
        {
            var @event = await service.CreateCertificateAsync(new CreateCertificateCommand
            {
                Name = "Persist Gen Test",
                ValidityDays = 365,
                KeyType = CertificateKeyType.NistP521,
                Notes = "Generated cert notes",
                SpaceId = 1
            }, CancellationToken.None).ConfigureAwait(false);

            var entity = await provider.GetCertificateByIdAsync(@event.Data.Id, CancellationToken.None).ConfigureAwait(false);

            entity.ShouldNotBeNull();
            entity.Name.ShouldBe("Persist Gen Test");
            entity.CertificateData.ShouldNotBeNullOrEmpty();
            entity.Password.ShouldNotBeNullOrEmpty();
            entity.HasPrivateKey.ShouldBeTrue();
            entity.SelfSigned.ShouldBeTrue();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Mediator_GenerateSelfSigned_ReturnsResponse()
    {
        await Run<IMediator>(async mediator =>
        {
            var response = await mediator.SendAsync<CreateCertificateCommand, CreateCertificateResponse>(
                new CreateCertificateCommand
                {
                    Name = "Mediator Gen Test",
                    ValidityDays = 365,
                    KeyType = CertificateKeyType.NistP256,
                    SpaceId = 1
                }).ConfigureAwait(false);

            response.Data.Certificate.ShouldNotBeNull();
            response.Data.Certificate.SubjectCommonName.ShouldBe("Mediator Gen Test");
            response.Data.Certificate.SelfSigned.ShouldBeTrue();
            response.Data.Certificate.HasPrivateKey.ShouldBeTrue();
        }).ConfigureAwait(false);
    }
}
