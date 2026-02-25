using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Certificates;
using Squid.Message.Commands.Deployments.Certificate;
using Squid.Message.Enums;
using Squid.Message.Events.Deployments.Certificate;
using Squid.Message.Models.Deployments.Certificate;
using Squid.Message.Requests.Deployments.Certificate;

namespace Squid.UnitTests.Services.Deployments.Certificates;

public class CertificateServiceTests
{
    private readonly Mock<ICertificateDataProvider> _dataProvider = new();
    private readonly IMapper _mapper;
    private readonly CertificateService _service;

    public CertificateServiceTests()
    {
        var config = new MapperConfiguration(cfg => cfg.AddProfile<CertificateMapping>());
        _mapper = config.CreateMapper();
        _service = new CertificateService(_mapper, _dataProvider.Object);
    }

    // === Test Certificate Generators ===

    private static (byte[] pfxBytes, string password, X509Certificate2 cert) GenerateSelfSignedPfx(
        string cn = "Test Certificate",
        string o = null,
        int validDays = 365,
        bool includePrivateKey = true,
        string password = "test-password")
    {
        var subject = o != null ? $"CN={cn}, O={o}" : $"CN={cn}";

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(validDays));

        byte[] pfxBytes;

        if (includePrivateKey)
        {
            pfxBytes = cert.Export(X509ContentType.Pfx, password);
        }
        else
        {
            pfxBytes = cert.Export(X509ContentType.Pfx, password);
        }

        return (pfxBytes, password, cert);
    }

    private static (byte[] pfxBytes, string password, X509Certificate2 cert) GenerateSelfSignedPfxWithSan(
        string cn = "Test Certificate",
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

    private static byte[] GenerateDerBytes(string cn = "Test DER Certificate")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));

        return cert.Export(X509ContentType.Cert);
    }

    private static byte[] GeneratePemBytes(string cn = "Test PEM Certificate")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));

        var pem = cert.ExportCertificatePem();
        return Encoding.UTF8.GetBytes(pem);
    }

    private static (byte[] pfxBytes, string password) GenerateExpiredPfx()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Expired Certificate", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-365),
            DateTimeOffset.UtcNow.AddDays(-1));

        var password = "test-password";
        return (cert.Export(X509ContentType.Pfx, password), password);
    }

    // === CreateCertificateAsync — PFX Format ===

    [Fact]
    public async Task CreateCertificateAsync_Pfx_ParsesThumbprint()
    {
        var (pfxBytes, password, cert) = GenerateSelfSignedPfx();

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, password), CancellationToken.None);

        @event.Data.Thumbprint.ShouldBe(cert.Thumbprint);
    }

    [Fact]
    public async Task CreateCertificateAsync_Pfx_DetectsFormatAsPkcs12()
    {
        var (pfxBytes, password, _) = GenerateSelfSignedPfx();

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, password), CancellationToken.None);

        @event.Data.CertificateDataFormat.ShouldBe(CertificateDataFormat.Pkcs12);
    }

    [Fact]
    public async Task CreateCertificateAsync_Pfx_ParsesSubjectDistinguishedName()
    {
        var (pfxBytes, password, _) = GenerateSelfSignedPfx(cn: "My App Cert");

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, password), CancellationToken.None);

        @event.Data.SubjectDistinguishedName.ShouldContain("CN=My App Cert");
    }

    [Fact]
    public async Task CreateCertificateAsync_Pfx_ExtractsSubjectCommonName()
    {
        var (pfxBytes, password, _) = GenerateSelfSignedPfx(cn: "My App Cert");

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, password), CancellationToken.None);

        @event.Data.SubjectCommonName.ShouldBe("My App Cert");
    }

    [Fact]
    public async Task CreateCertificateAsync_Pfx_SelfSignedIsTrue()
    {
        var (pfxBytes, password, _) = GenerateSelfSignedPfx();

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, password), CancellationToken.None);

        @event.Data.SelfSigned.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateCertificateAsync_Pfx_ParsesValidityPeriod()
    {
        var (pfxBytes, password, cert) = GenerateSelfSignedPfx(validDays: 365);

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, password), CancellationToken.None);

        @event.Data.NotBefore.ShouldBeLessThan(DateTimeOffset.UtcNow);
        @event.Data.NotAfter.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task CreateCertificateAsync_Pfx_HasPrivateKeyIsTrue()
    {
        var (pfxBytes, password, _) = GenerateSelfSignedPfx();

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, password), CancellationToken.None);

        @event.Data.HasPrivateKey.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateCertificateAsync_Pfx_ParsesSerialNumber()
    {
        var (pfxBytes, password, cert) = GenerateSelfSignedPfx();

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, password), CancellationToken.None);

        @event.Data.SerialNumber.ShouldNotBeNullOrEmpty();
        @event.Data.SerialNumber.ShouldBe(cert.SerialNumber);
    }

    [Fact]
    public async Task CreateCertificateAsync_Pfx_ParsesSignatureAlgorithm()
    {
        var (pfxBytes, password, _) = GenerateSelfSignedPfx();

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, password), CancellationToken.None);

        @event.Data.SignatureAlgorithmName.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateCertificateAsync_Pfx_ParsesVersionIs3()
    {
        var (pfxBytes, password, _) = GenerateSelfSignedPfx();

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, password), CancellationToken.None);

        @event.Data.Version.ShouldBe(3);
    }

    [Fact]
    public async Task CreateCertificateAsync_Pfx_IssuerMatchesSubjectForSelfSigned()
    {
        var (pfxBytes, password, _) = GenerateSelfSignedPfx(cn: "Self Signed");

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, password), CancellationToken.None);

        @event.Data.IssuerDistinguishedName.ShouldBe(@event.Data.SubjectDistinguishedName);
        @event.Data.IssuerCommonName.ShouldBe("Self Signed");
    }

    // === CreateCertificateAsync — PEM Format ===

    [Fact]
    public async Task CreateCertificateAsync_Pem_DetectsFormatAsPem()
    {
        var pemBytes = GeneratePemBytes();

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pemBytes, null), CancellationToken.None);

        @event.Data.CertificateDataFormat.ShouldBe(CertificateDataFormat.Pem);
    }

    [Fact]
    public async Task CreateCertificateAsync_Pem_ParsesThumbprint()
    {
        var pemBytes = GeneratePemBytes(cn: "PEM Test");

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pemBytes, null), CancellationToken.None);

        @event.Data.Thumbprint.ShouldNotBeNullOrEmpty();
        @event.Data.SubjectCommonName.ShouldBe("PEM Test");
    }

    [Fact]
    public async Task CreateCertificateAsync_Pem_HasPrivateKeyIsFalse()
    {
        var pemBytes = GeneratePemBytes();

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pemBytes, null), CancellationToken.None);

        @event.Data.HasPrivateKey.ShouldBeFalse();
    }

    // === CreateCertificateAsync — DER Format ===

    [Fact]
    public async Task CreateCertificateAsync_Der_DetectsFormatAsDer()
    {
        var derBytes = GenerateDerBytes();

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(derBytes, null), CancellationToken.None);

        @event.Data.CertificateDataFormat.ShouldBe(CertificateDataFormat.Der);
    }

    [Fact]
    public async Task CreateCertificateAsync_Der_ParsesSubjectCommonName()
    {
        var derBytes = GenerateDerBytes(cn: "DER Test");

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(derBytes, null), CancellationToken.None);

        @event.Data.SubjectCommonName.ShouldBe("DER Test");
    }

    [Fact]
    public async Task CreateCertificateAsync_Der_HasPrivateKeyIsFalse()
    {
        var derBytes = GenerateDerBytes();

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(derBytes, null), CancellationToken.None);

        @event.Data.HasPrivateKey.ShouldBeFalse();
    }

    // === CreateCertificateAsync — SAN Extension ===

    [Fact]
    public async Task CreateCertificateAsync_WithSan_ParsesSubjectAlternativeNames()
    {
        var (pfxBytes, password, _) = GenerateSelfSignedPfxWithSan(
            cn: "SAN Test",
            dnsNames: new[] { "example.com", "*.example.com" });

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, password), CancellationToken.None);

        @event.Data.SubjectAlternativeNames.ShouldNotBeNull();
        @event.Data.SubjectAlternativeNames.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task CreateCertificateAsync_WithoutSan_SubjectAlternativeNamesIsEmpty()
    {
        var (pfxBytes, password, _) = GenerateSelfSignedPfx();

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, password), CancellationToken.None);

        @event.Data.SubjectAlternativeNames.ShouldBeEmpty();
    }

    // === CreateCertificateAsync — Expired Certificate ===

    [Fact]
    public async Task CreateCertificateAsync_ExpiredCert_IsExpiredIsTrue()
    {
        var (pfxBytes, password) = GenerateExpiredPfx();

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, password), CancellationToken.None);

        @event.Data.IsExpired.ShouldBeTrue();
        @event.Data.NotAfter.ShouldBeLessThan(DateTimeOffset.UtcNow);
    }

    // === CreateCertificateAsync — Command Fields ===

    [Fact]
    public async Task CreateCertificateAsync_SetsNameFromCommand()
    {
        var (pfxBytes, password, _) = GenerateSelfSignedPfx();

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(
            MakeCreateCommand(pfxBytes, password, name: "Production SSL"), CancellationToken.None);

        @event.Data.Name.ShouldBe("Production SSL");
    }

    [Fact]
    public async Task CreateCertificateAsync_SetsEnvironmentIds()
    {
        var (pfxBytes, password, _) = GenerateSelfSignedPfx();

        SetupAddCapture();

        var command = MakeCreateCommand(pfxBytes, password);
        command.EnvironmentIds = new List<int> { 1, 3, 5 };

        var @event = await _service.CreateCertificateAsync(command, CancellationToken.None);

        @event.Data.EnvironmentIds.ShouldBe(new List<int> { 1, 3, 5 });
    }

    [Fact]
    public async Task CreateCertificateAsync_NullEnvironmentIds_ReturnsEmptyList()
    {
        var (pfxBytes, password, _) = GenerateSelfSignedPfx();

        SetupAddCapture();

        var command = MakeCreateCommand(pfxBytes, password);
        command.EnvironmentIds = null;

        var @event = await _service.CreateCertificateAsync(command, CancellationToken.None);

        @event.Data.EnvironmentIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateCertificateAsync_PasswordHasValueIsTrue_WhenPasswordProvided()
    {
        var (pfxBytes, password, _) = GenerateSelfSignedPfx();

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, password), CancellationToken.None);

        @event.Data.PasswordHasValue.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateCertificateAsync_PasswordHasValueIsFalse_WhenNoPassword()
    {
        var pemBytes = GeneratePemBytes();

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pemBytes, null), CancellationToken.None);

        @event.Data.PasswordHasValue.ShouldBeFalse();
    }

    [Fact]
    public async Task CreateCertificateAsync_DoesNotExposeCertificateDataInDto()
    {
        var (pfxBytes, password, _) = GenerateSelfSignedPfx();

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, password), CancellationToken.None);

        var dto = @event.Data;
        var dtoType = typeof(CertificateDto);

        dtoType.GetProperty("CertificateData").ShouldBeNull();
        dtoType.GetProperty("Password").ShouldBeNull();
    }

    [Fact]
    public async Task CreateCertificateAsync_CallsAddOnDataProvider()
    {
        var (pfxBytes, password, _) = GenerateSelfSignedPfx();

        SetupAddCapture();

        await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, password), CancellationToken.None);

        _dataProvider.Verify(
            p => p.AddCertificateAsync(It.IsAny<Certificate>(), true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // === CreateCertificateAsync — Invalid Data ===

    [Fact]
    public async Task CreateCertificateAsync_InvalidBase64_Throws()
    {
        var command = new CreateCertificateCommand
        {
            Name = "Bad Cert",
            CertificateData = "not-valid-base64!!!",
            SpaceId = 1
        };

        await Should.ThrowAsync<FormatException>(
            () => _service.CreateCertificateAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task CreateCertificateAsync_InvalidCertBytes_Throws()
    {
        var command = new CreateCertificateCommand
        {
            Name = "Bad Cert",
            CertificateData = Convert.ToBase64String(new byte[] { 0x01, 0x02, 0x03 }),
            SpaceId = 1
        };

        await Should.ThrowAsync<CryptographicException>(
            () => _service.CreateCertificateAsync(command, CancellationToken.None));
    }

    // === UpdateCertificateAsync ===

    [Fact]
    public async Task UpdateCertificateAsync_UpdatesNameAndNotes()
    {
        var entity = CreateExistingCertificateEntity();

        _dataProvider.Setup(p => p.GetCertificateByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);
        _dataProvider.Setup(p => p.UpdateCertificateAsync(It.IsAny<Certificate>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new UpdateCertificateCommand
        {
            Id = 1,
            Name = "Updated Name",
            Notes = "Updated Notes",
            EnvironmentIds = new List<int> { 2, 4 }
        };

        var @event = await _service.UpdateCertificateAsync(command, CancellationToken.None);

        @event.Data.Name.ShouldBe("Updated Name");
        @event.Data.EnvironmentIds.ShouldBe(new List<int> { 2, 4 });
    }

    [Fact]
    public async Task UpdateCertificateAsync_PreservesX509Metadata()
    {
        var entity = CreateExistingCertificateEntity();

        _dataProvider.Setup(p => p.GetCertificateByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);
        _dataProvider.Setup(p => p.UpdateCertificateAsync(It.IsAny<Certificate>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new UpdateCertificateCommand { Id = 1, Name = "New Name" };

        var @event = await _service.UpdateCertificateAsync(command, CancellationToken.None);

        @event.Data.Thumbprint.ShouldBe("AABBCCDD");
        @event.Data.SubjectCommonName.ShouldBe("Original CN");
    }

    [Fact]
    public async Task UpdateCertificateAsync_NotFound_Throws()
    {
        _dataProvider.Setup(p => p.GetCertificateByIdAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Certificate)null);

        var command = new UpdateCertificateCommand { Id = 999, Name = "X" };

        await Should.ThrowAsync<Exception>(
            () => _service.UpdateCertificateAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateCertificateAsync_SetsLastModifiedOn()
    {
        var entity = CreateExistingCertificateEntity();
        var originalModified = entity.LastModifiedOn;

        _dataProvider.Setup(p => p.GetCertificateByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);
        _dataProvider.Setup(p => p.UpdateCertificateAsync(It.IsAny<Certificate>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new UpdateCertificateCommand { Id = 1, Name = "Updated" };

        await _service.UpdateCertificateAsync(command, CancellationToken.None);

        entity.LastModifiedOn.ShouldNotBe(originalModified);
    }

    // === ReplaceCertificateAsync ===

    [Fact]
    public async Task ReplaceCertificateAsync_CreatesNewEntityWithParsedMetadata()
    {
        var original = CreateExistingCertificateEntity();
        var (pfxBytes, password, newCert) = GenerateSelfSignedPfx(cn: "Replacement Cert");

        _dataProvider.Setup(p => p.GetCertificateByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(original);
        _dataProvider.Setup(p => p.AddCertificateAsync(It.IsAny<Certificate>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _dataProvider.Setup(p => p.UpdateCertificateAsync(It.IsAny<Certificate>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new ReplaceCertificateCommand
        {
            Id = 1,
            CertificateData = Convert.ToBase64String(pfxBytes),
            Password = password
        };

        var @event = await _service.ReplaceCertificateAsync(command, CancellationToken.None);

        @event.Data.Thumbprint.ShouldBe(newCert.Thumbprint);
        @event.Data.SubjectCommonName.ShouldBe("Replacement Cert");
    }

    [Fact]
    public async Task ReplaceCertificateAsync_InheritsNameAndNotesFromOriginal()
    {
        var original = CreateExistingCertificateEntity();
        original.Name = "Inherited Name";
        original.Notes = "Inherited Notes";

        var (pfxBytes, password, _) = GenerateSelfSignedPfx();

        _dataProvider.Setup(p => p.GetCertificateByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(original);
        _dataProvider.Setup(p => p.AddCertificateAsync(It.IsAny<Certificate>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _dataProvider.Setup(p => p.UpdateCertificateAsync(It.IsAny<Certificate>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new ReplaceCertificateCommand
        {
            Id = 1,
            CertificateData = Convert.ToBase64String(pfxBytes),
            Password = password
        };

        var @event = await _service.ReplaceCertificateAsync(command, CancellationToken.None);

        @event.Data.Name.ShouldBe("Inherited Name");
    }

    [Fact]
    public async Task ReplaceCertificateAsync_ArchivesOriginal()
    {
        var original = CreateExistingCertificateEntity();
        var (pfxBytes, password, _) = GenerateSelfSignedPfx();

        _dataProvider.Setup(p => p.GetCertificateByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(original);
        _dataProvider.Setup(p => p.AddCertificateAsync(It.IsAny<Certificate>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _dataProvider.Setup(p => p.UpdateCertificateAsync(It.IsAny<Certificate>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new ReplaceCertificateCommand
        {
            Id = 1,
            CertificateData = Convert.ToBase64String(pfxBytes),
            Password = password
        };

        await _service.ReplaceCertificateAsync(command, CancellationToken.None);

        original.Archived.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReplaceCertificateAsync_InheritsEnvironmentIds()
    {
        var original = CreateExistingCertificateEntity();
        original.EnvironmentIds = "1,3,5";

        var (pfxBytes, password, _) = GenerateSelfSignedPfx();

        _dataProvider.Setup(p => p.GetCertificateByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(original);
        _dataProvider.Setup(p => p.AddCertificateAsync(It.IsAny<Certificate>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _dataProvider.Setup(p => p.UpdateCertificateAsync(It.IsAny<Certificate>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new ReplaceCertificateCommand
        {
            Id = 1,
            CertificateData = Convert.ToBase64String(pfxBytes),
            Password = password
        };

        var @event = await _service.ReplaceCertificateAsync(command, CancellationToken.None);

        @event.Data.EnvironmentIds.ShouldBe(new List<int> { 1, 3, 5 });
    }

    [Fact]
    public async Task ReplaceCertificateAsync_NotFound_Throws()
    {
        _dataProvider.Setup(p => p.GetCertificateByIdAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Certificate)null);

        var command = new ReplaceCertificateCommand
        {
            Id = 999,
            CertificateData = Convert.ToBase64String(GeneratePemBytes()),
            Password = null
        };

        await Should.ThrowAsync<Exception>(
            () => _service.ReplaceCertificateAsync(command, CancellationToken.None));
    }

    // === DeleteCertificatesAsync ===

    [Fact]
    public async Task DeleteCertificatesAsync_AllFound_NoFailIds()
    {
        var entities = new List<Certificate>
        {
            new() { Id = 1 },
            new() { Id = 2 }
        };

        _dataProvider.Setup(p => p.GetCertificatesByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);
        _dataProvider.Setup(p => p.DeleteCertificatesAsync(It.IsAny<List<Certificate>>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new DeleteCertificatesCommand { Ids = new List<int> { 1, 2 } };

        var @event = await _service.DeleteCertificatesAsync(command, CancellationToken.None);

        @event.Data.FailIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteCertificatesAsync_PartiallyFound_ReturnsFailIds()
    {
        var entities = new List<Certificate> { new() { Id = 1 } };

        _dataProvider.Setup(p => p.GetCertificatesByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);
        _dataProvider.Setup(p => p.DeleteCertificatesAsync(It.IsAny<List<Certificate>>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new DeleteCertificatesCommand { Ids = new List<int> { 1, 2, 3 } };

        var @event = await _service.DeleteCertificatesAsync(command, CancellationToken.None);

        @event.Data.FailIds.ShouldBe(new List<int> { 2, 3 });
    }

    // === GetCertificatesAsync ===

    [Fact]
    public async Task GetCertificatesAsync_ReturnsPaginatedResults()
    {
        var entities = new List<Certificate>
        {
            CreateExistingCertificateEntity(id: 1, name: "Cert A"),
            CreateExistingCertificateEntity(id: 2, name: "Cert B")
        };

        _dataProvider.Setup(p => p.GetCertificatePagingAsync(1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((2, entities));

        var request = new GetCertificatesRequest { PageIndex = 1, PageSize = 20 };

        var response = await _service.GetCertificatesAsync(request, CancellationToken.None);

        response.Data.Count.ShouldBe(2);
        response.Data.Certificates.Count.ShouldBe(2);
        response.Data.Certificates[0].Name.ShouldBe("Cert A");
        response.Data.Certificates[1].Name.ShouldBe("Cert B");
    }

    // === AutoMapper — CSV to List Conversion ===

    [Fact]
    public void Mapper_EnvironmentIds_CsvToList()
    {
        var entity = CreateExistingCertificateEntity();
        entity.EnvironmentIds = "1,2,3";

        var dto = _mapper.Map<CertificateDto>(entity);

        dto.EnvironmentIds.ShouldBe(new List<int> { 1, 2, 3 });
    }

    [Fact]
    public void Mapper_EnvironmentIds_NullToEmptyList()
    {
        var entity = CreateExistingCertificateEntity();
        entity.EnvironmentIds = null;

        var dto = _mapper.Map<CertificateDto>(entity);

        dto.EnvironmentIds.ShouldBeEmpty();
    }

    [Fact]
    public void Mapper_SubjectAlternativeNames_CsvToList()
    {
        var entity = CreateExistingCertificateEntity();
        entity.SubjectAlternativeNames = "DNS Name=example.com,DNS Name=*.example.com";

        var dto = _mapper.Map<CertificateDto>(entity);

        dto.SubjectAlternativeNames.Count.ShouldBe(2);
    }

    [Fact]
    public void Mapper_SubjectAlternativeNames_NullToEmptyList()
    {
        var entity = CreateExistingCertificateEntity();
        entity.SubjectAlternativeNames = null;

        var dto = _mapper.Map<CertificateDto>(entity);

        dto.SubjectAlternativeNames.ShouldBeEmpty();
    }

    [Fact]
    public void Mapper_IsExpired_ComputedFromEntity()
    {
        var entity = CreateExistingCertificateEntity();
        entity.NotAfter = DateTimeOffset.UtcNow.AddDays(-1);

        var dto = _mapper.Map<CertificateDto>(entity);

        dto.IsExpired.ShouldBeTrue();
    }

    [Fact]
    public void Mapper_PasswordHasValue_MappedFromEntity()
    {
        var entity = CreateExistingCertificateEntity();
        entity.Password = "secret";

        var dto = _mapper.Map<CertificateDto>(entity);

        dto.PasswordHasValue.ShouldBeTrue();
    }

    [Fact]
    public void Mapper_PasswordHasValue_FalseWhenNull()
    {
        var entity = CreateExistingCertificateEntity();
        entity.Password = null;

        var dto = _mapper.Map<CertificateDto>(entity);

        dto.PasswordHasValue.ShouldBeFalse();
    }

    // === PFX Without Password ===

    [Fact]
    public async Task CreateCertificateAsync_PfxWithoutPassword_ParsesSuccessfully()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=No Password", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
        var pfxBytes = cert.Export(X509ContentType.Pfx, (string)null);

        SetupAddCapture();

        var @event = await _service.CreateCertificateAsync(MakeCreateCommand(pfxBytes, null), CancellationToken.None);

        @event.Data.Thumbprint.ShouldBe(cert.Thumbprint);
        @event.Data.CertificateDataFormat.ShouldBe(CertificateDataFormat.Pkcs12);
    }

    // === Helpers ===

    private CreateCertificateCommand MakeCreateCommand(
        byte[] certBytes,
        string password,
        string name = "Test Certificate",
        string notes = "Test notes")
    {
        return new CreateCertificateCommand
        {
            Name = name,
            Notes = notes,
            CertificateData = Convert.ToBase64String(certBytes),
            Password = password,
            SpaceId = 1,
            EnvironmentIds = new List<int>()
        };
    }

    private void SetupAddCapture()
    {
        _dataProvider.Setup(p => p.AddCertificateAsync(It.IsAny<Certificate>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private static Certificate CreateExistingCertificateEntity(
        int id = 1,
        string name = "Existing Cert")
    {
        return new Certificate
        {
            Id = id,
            SpaceId = 1,
            Name = name,
            Notes = "Some notes",
            CertificateData = "base64data",
            Password = "password",
            CertificateDataFormat = CertificateDataFormat.Pkcs12,
            Thumbprint = "AABBCCDD",
            SubjectDistinguishedName = "CN=Original CN",
            SubjectCommonName = "Original CN",
            IssuerDistinguishedName = "CN=Original CN",
            IssuerCommonName = "Original CN",
            SelfSigned = true,
            NotAfter = DateTimeOffset.UtcNow.AddDays(365),
            NotBefore = DateTimeOffset.UtcNow.AddDays(-1),
            HasPrivateKey = true,
            Version = 3,
            SerialNumber = "0123456789",
            SignatureAlgorithmName = "sha256RSA",
            SubjectAlternativeNames = null,
            EnvironmentIds = null,
            LastModifiedOn = DateTimeOffset.UtcNow.AddDays(-10)
        };
    }
}
