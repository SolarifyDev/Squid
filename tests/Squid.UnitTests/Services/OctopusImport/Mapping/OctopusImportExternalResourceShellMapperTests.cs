using System.Linq;
using System.Text.Json;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Mapping;

public class OctopusImportExternalResourceShellMapperTests
{
    private readonly OctopusImportExternalResourceShellMapper _mapper = new();

    [Fact]
    public void MapAccountToCreateCommand_CreatesEmptyCredentialShellAndManualMarker()
    {
        var account = new OctopusAccountDto
        {
            Id = "Accounts-1",
            Name = "AWS prod",
            AccountType = "AmazonWebServicesAccount",
            EnvironmentIds = ["Environments-1"],
            Credentials = Json("""
                {
                  "AccessKey": "source-access-key",
                  "SecretKey": "source-secret-key"
                }
                """)
        };
        var idMap = new OctopusImportIdMap();
        idMap.AddReused(Resource(new OctopusEnvironmentDto
        {
            Id = "Environments-1",
            Name = "Production"
        }, OctopusResourceKind.Environment), 21);

        var result = _mapper.MapAccountToCreateCommand(Resource(account, OctopusResourceKind.Account), idMap, 7);

        result.HasBlockers.ShouldBeFalse();
        result.CreateCommand.ShouldNotBeNull();
        result.CreateCommand.SpaceId.ShouldBe(7);
        result.CreateCommand.Name.ShouldBe("AWS prod");
        result.CreateCommand.AccountType.ShouldBe(AccountType.AmazonWebServicesAccount);
        result.CreateCommand.EnvironmentIds.ShouldBe([21]);
        result.CreateCommand.Credentials.Value.GetRawText().ShouldNotContain("source-access-key");
        result.CreateCommand.Credentials.Value.GetRawText().ShouldNotContain("source-secret-key");
        result.ManualConfigurationMarkers.Single().ReasonCode.ShouldBe(OctopusImportRedactionDiagnosticCodes.AccountCredentialsOmitted);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportRedactionDiagnosticCodes.AccountCredentialsOmitted);
        result.Diagnostics.Single().Message.ShouldNotContain("source-secret-key");
    }

    [Fact]
    public void MapCertificateToManualShell_DoesNotCreateCertificateDataAndMarksPrivateMaterial()
    {
        var certificate = new OctopusCertificateDto
        {
            Id = "Certificates-1",
            Name = "TLS cert",
            Notes = "Imported shell",
            HasPrivateKey = true,
            NotBefore = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            NotAfter = DateTimeOffset.Parse("2027-01-01T00:00:00Z"),
            CertificateData = Json("""{ "Pfx": "certificate-source-secret" }""")
        };

        var result = _mapper.MapCertificateToManualShell(Resource(certificate, OctopusResourceKind.Certificate));

        result.HasBlockers.ShouldBeFalse();
        result.Shell.Name.ShouldBe("TLS cert");
        result.Shell.SourceHasPrivateKey.ShouldBeTrue();
        result.Shell.CannotExecuteUntilConfigured.ShouldBeTrue();
        result.ManualConfigurationMarkers.Single().FieldName.ShouldBe(OctopusImportManualConfiguration.CertificatePrivateMaterialField);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportRedactionDiagnosticCodes.CertificatePrivateMaterialOmitted);
        result.Diagnostics.Single().Message.ShouldNotContain("certificate-source-secret");
    }

    [Fact]
    public void MapTargetToManualShell_PreservesSafeEndpointMetadataAndOmitsEndpointSecrets()
    {
        var target = new OctopusMachineDto
        {
            Id = "Machines-1",
            Name = "EKS target",
            Roles = ["aws-eks-us"],
            EnvironmentIds = ["Environments-1"],
            Endpoint = Json("""
                {
                  "CommunicationStyle": "KubernetesApi",
                  "ClusterUrl": "https://cluster.example",
                  "Namespace": "production",
                  "ProviderType": "AwsEks",
                  "ProviderConfig": { "SecretKey": "endpoint-source-secret" },
                  "SubscriptionId": "subscription-source-secret"
                }
                """)
        };
        var idMap = new OctopusImportIdMap();
        idMap.AddReused(Resource(new OctopusEnvironmentDto
        {
            Id = "Environments-1",
            Name = "Production"
        }, OctopusResourceKind.Environment), 21);

        var result = _mapper.MapTargetToManualShell(Resource(target, OctopusResourceKind.Machine), idMap, 7);

        result.HasBlockers.ShouldBeFalse();
        result.Shell.Name.ShouldBe("EKS target");
        result.Shell.Roles.ShouldBe(["aws-eks-us"]);
        result.Shell.EnvironmentIds.ShouldBe([21]);
        result.Shell.CommunicationStyle.ShouldBe("KubernetesApi");
        result.Shell.EndpointMetadata["ClusterUrl"].ShouldBe("https://cluster.example");
        result.Shell.EndpointMetadata["Namespace"].ShouldBe("production");
        result.Shell.EndpointMetadata.ContainsKey("ProviderConfig").ShouldBeFalse();
        result.Shell.EndpointMetadata.ContainsKey("SubscriptionId").ShouldBeFalse();
        result.Shell.CannotExecuteUntilConfigured.ShouldBeTrue();
        result.ManualConfigurationMarkers.Single().ReasonCode.ShouldBe(OctopusImportRedactionDiagnosticCodes.EndpointSecretOmitted);
        result.Diagnostics.Single().Message.ShouldNotContain("endpoint-source-secret");
    }

    [Fact]
    public void MapAccountToCreateCommand_WhenEnvironmentMappingIsMissing_AddsBlocker()
    {
        var account = new OctopusAccountDto
        {
            Id = "Accounts-1",
            Name = "Token account",
            AccountType = "Token",
            EnvironmentIds = ["Environments-Missing"],
            Credentials = Json("""{ "Token": "source-token" }""")
        };

        var result = _mapper.MapAccountToCreateCommand(Resource(account, OctopusResourceKind.Account), new OctopusImportIdMap(), 7);

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Select(d => d.Code).ShouldContain("OctopusImport.ExternalResource.MissingEnvironmentMapping");
        result.Diagnostics.All(d => d.Message.Contains("source-token", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static OctopusResourceNode Resource<T>(T source, OctopusResourceKind kind)
        where T : OctopusDocumentDto
        => new(
            source.Id,
            source.Name,
            kind,
            DocumentKind(kind),
            $"{source.Id}.json",
            null,
            null,
            false,
            source);

    private static OctopusDocumentKind DocumentKind(OctopusResourceKind kind)
        => kind switch
        {
            OctopusResourceKind.Environment => OctopusDocumentKind.Environment,
            OctopusResourceKind.Account => OctopusDocumentKind.Account,
            OctopusResourceKind.Certificate => OctopusDocumentKind.Certificate,
            OctopusResourceKind.Machine => OctopusDocumentKind.Machine,
            _ => OctopusDocumentKind.Unknown
        };
}
