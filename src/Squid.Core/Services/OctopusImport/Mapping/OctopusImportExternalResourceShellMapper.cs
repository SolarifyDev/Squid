using System.Globalization;
using System.Text.Json;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Commands.Deployments.Account;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping;

public interface IOctopusImportExternalResourceShellMapper : IScopedDependency
{
    OctopusImportAccountShellMappingResult MapAccountToCreateCommand(
        OctopusResourceNode accountResource,
        OctopusImportIdMap idMap,
        int destinationSpaceId);

    OctopusImportCertificateShellMappingResult MapCertificateToManualShell(
        OctopusResourceNode certificateResource);

    OctopusImportTargetShellMappingResult MapTargetToManualShell(
        OctopusResourceNode targetResource,
        OctopusImportIdMap idMap,
        int destinationSpaceId);
}

public sealed class OctopusImportExternalResourceShellMapper : IOctopusImportExternalResourceShellMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly IReadOnlySet<string> SafeEndpointMetadataNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CommunicationStyle",
        "ClusterUrl",
        "Namespace",
        "SkipTlsVerification",
        "ProviderType",
        "Uri",
        "Host",
        "Port",
        "Fingerprint",
        "Thumbprint",
        "RemoteWorkingDirectory",
        "AgentVersion",
        "ReleaseName",
        "HelmNamespace",
        "ChartRef"
    };

    public OctopusImportAccountShellMappingResult MapAccountToCreateCommand(
        OctopusResourceNode accountResource,
        OctopusImportIdMap idMap,
        int destinationSpaceId)
    {
        ArgumentNullException.ThrowIfNull(accountResource);
        ArgumentNullException.ThrowIfNull(idMap);

        if (accountResource.Kind != OctopusResourceKind.Account)
            throw new ArgumentException("Octopus account shell mapper requires an account resource.", nameof(accountResource));

        if (destinationSpaceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationSpaceId), destinationSpaceId, "Destination space id must be positive.");

        var account = accountResource.GetSource<OctopusAccountDto>()
            ?? throw new ArgumentException("Octopus account resource does not contain an OctopusAccountDto source.", nameof(accountResource));

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        var markers = new List<OctopusImportManualConfigurationMarker>();
        var accountType = MapAccountType(account.AccountType, accountResource, diagnostics);
        var environmentIds = MapEnvironmentIds(account.EnvironmentIds, idMap, accountResource, diagnostics);

        if (OctopusImportManualConfiguration.HasJsonPayload(account.Credentials))
        {
            markers.Add(OctopusImportManualConfiguration.CreateMarker(
                accountResource,
                OctopusImportManualConfiguration.AccountCredentialsField,
                OctopusImportRedactionDiagnosticCodes.AccountCredentialsOmitted));

            diagnostics.Add(OctopusImportManualConfiguration.Diagnostic(
                OctopusImportCompatibilitySeverity.Warning,
                OctopusImportRedactionDiagnosticCodes.AccountCredentialsOmitted,
                "Octopus account credentials were omitted from the Squid DeploymentAccount shell and must be configured manually after import.",
                accountResource));
        }

        var createCommand = accountType == null
            ? null
            : new CreateDeploymentAccountCommand
            {
                SpaceId = destinationSpaceId,
                Name = account.Name,
                AccountType = accountType.Value,
                Credentials = CreateEmptyCredentialsElement(accountType.Value),
                EnvironmentIds = environmentIds
            };

        return new OctopusImportAccountShellMappingResult(createCommand, diagnostics, markers);
    }

    public OctopusImportCertificateShellMappingResult MapCertificateToManualShell(
        OctopusResourceNode certificateResource)
    {
        ArgumentNullException.ThrowIfNull(certificateResource);

        if (certificateResource.Kind != OctopusResourceKind.Certificate)
            throw new ArgumentException("Octopus certificate shell mapper requires a certificate resource.", nameof(certificateResource));

        var certificate = certificateResource.GetSource<OctopusCertificateDto>()
            ?? throw new ArgumentException("Octopus certificate resource does not contain an OctopusCertificateDto source.", nameof(certificateResource));

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        var markers = new List<OctopusImportManualConfigurationMarker>();

        if (certificate.HasPrivateKey || OctopusImportManualConfiguration.HasJsonPayload(certificate.CertificateData))
        {
            markers.Add(OctopusImportManualConfiguration.CreateMarker(
                certificateResource,
                OctopusImportManualConfiguration.CertificatePrivateMaterialField,
                OctopusImportRedactionDiagnosticCodes.CertificatePrivateMaterialOmitted));

            diagnostics.Add(OctopusImportManualConfiguration.Diagnostic(
                OctopusImportCompatibilitySeverity.Warning,
                OctopusImportRedactionDiagnosticCodes.CertificatePrivateMaterialOmitted,
                "Octopus certificate private material was omitted and the certificate must be recreated manually after import.",
                certificateResource));
        }

        var shell = new OctopusImportCertificateShell(
            certificate.Name,
            certificate.Notes,
            certificate.HasPrivateKey,
            certificate.NotBefore,
            certificate.NotAfter,
            CannotExecuteUntilConfigured: markers.Any(m => m.IsRequired));

        return new OctopusImportCertificateShellMappingResult(shell, diagnostics, markers);
    }

    public OctopusImportTargetShellMappingResult MapTargetToManualShell(
        OctopusResourceNode targetResource,
        OctopusImportIdMap idMap,
        int destinationSpaceId)
    {
        ArgumentNullException.ThrowIfNull(targetResource);
        ArgumentNullException.ThrowIfNull(idMap);

        if (targetResource.Kind != OctopusResourceKind.Machine)
            throw new ArgumentException("Octopus target shell mapper requires a machine resource.", nameof(targetResource));

        if (destinationSpaceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationSpaceId), destinationSpaceId, "Destination space id must be positive.");

        var target = targetResource.GetSource<OctopusMachineDto>()
            ?? throw new ArgumentException("Octopus target resource does not contain an OctopusMachineDto source.", nameof(targetResource));

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        var markers = new List<OctopusImportManualConfigurationMarker>();
        var environmentIds = MapEnvironmentIds(target.EnvironmentIds, idMap, targetResource, diagnostics);
        var endpointMetadata = BuildSafeEndpointMetadata(target.Endpoint);
        var communicationStyle = endpointMetadata.TryGetValue("CommunicationStyle", out var style)
            ? style
            : null;

        if (OctopusImportManualConfiguration.HasEndpointSecret(target.Endpoint))
        {
            markers.Add(OctopusImportManualConfiguration.CreateMarker(
                targetResource,
                OctopusImportManualConfiguration.EndpointSecretsField,
                OctopusImportRedactionDiagnosticCodes.EndpointSecretOmitted,
                metadata: new Dictionary<string, string>
                {
                    ["CommunicationStyle"] = communicationStyle
                }));

            diagnostics.Add(OctopusImportManualConfiguration.Diagnostic(
                OctopusImportCompatibilitySeverity.Warning,
                OctopusImportRedactionDiagnosticCodes.EndpointSecretOmitted,
                "Octopus target endpoint secrets were omitted and the deployment target must be configured manually after import.",
                targetResource));
        }

        var shell = new OctopusImportTargetShell(
            target.Name,
            target.IsDisabled,
            target.Roles ?? [],
            environmentIds,
            communicationStyle,
            endpointMetadata,
            CannotExecuteUntilConfigured: true);

        return new OctopusImportTargetShellMappingResult(shell, diagnostics, markers);
    }

    private static AccountType? MapAccountType(
        string sourceAccountType,
        OctopusResourceNode accountResource,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        if (Enum.TryParse<AccountType>(sourceAccountType?.Trim(), ignoreCase: true, out var direct)
            && direct != AccountType.None
            && direct != AccountType.AzureSubscription)
            return direct;

        var normalized = Normalize(sourceAccountType);
        var mapped = normalized switch
        {
            "usernamepassword" or "usernamepasswordaccount" => AccountType.UsernamePassword,
            "sshkeypair" or "sshkeypairaccount" => AccountType.SshKeyPair,
            "token" or "tokenaccount" => AccountType.Token,
            "azureserviceprincipal" or "azureserviceprincipalaccount" => AccountType.AzureServicePrincipal,
            "azureoidc" or "azureoidcaccount" => AccountType.AzureOidc,
            "amazonwebservicesaccount" or "awsaccount" => AccountType.AmazonWebServicesAccount,
            "amazonwebservicesroleaccount" or "awsroleaccount" => AccountType.AmazonWebServicesRoleAccount,
            "amazonwebservicesoidcaccount" or "awsoidcaccount" => AccountType.AmazonWebServicesOidcAccount,
            "clientcertificate" or "clientcertificateaccount" => AccountType.ClientCertificate,
            "googlecloudaccount" or "gcpaccount" => AccountType.GoogleCloudAccount,
            "openclawgateway" or "openclawgatewayaccount" => AccountType.OpenClawGateway,
            _ => (AccountType?)null
        };

        if (mapped != null)
            return mapped.Value;

        diagnostics.Add(OctopusImportManualConfiguration.Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            "OctopusImport.Account.UnsupportedAccountType",
            $"Octopus account type '{sourceAccountType}' is not supported by the current Squid import account shell mapper.",
            accountResource));

        return null;
    }

    private static List<int> MapEnvironmentIds(
        IEnumerable<string> sourceEnvironmentIds,
        OctopusImportIdMap idMap,
        OctopusResourceNode resource,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var environmentIds = new List<int>();

        foreach (var sourceEnvironmentId in sourceEnvironmentIds ?? [])
        {
            if (idMap.TryGetDestinationId(sourceEnvironmentId, OctopusResourceKind.Environment.ToString(), out var destinationEnvironmentId))
            {
                environmentIds.Add(destinationEnvironmentId);
                continue;
            }

            diagnostics.Add(OctopusImportManualConfiguration.Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                "OctopusImport.ExternalResource.MissingEnvironmentMapping",
                $"Octopus environment scope '{sourceEnvironmentId}' has not been mapped to a destination Squid environment.",
                resource));
        }

        return environmentIds;
    }

    private static JsonElement CreateEmptyCredentialsElement(AccountType accountType)
    {
        object credentials = accountType switch
        {
            AccountType.Token => new TokenCredentials(),
            AccountType.UsernamePassword => new UsernamePasswordCredentials(),
            AccountType.ClientCertificate => new ClientCertificateCredentials(),
            AccountType.AmazonWebServicesAccount => new AwsCredentials(),
            AccountType.AmazonWebServicesRoleAccount => new AwsRoleCredentials(),
            AccountType.SshKeyPair => new SshKeyPairCredentials(),
            AccountType.AzureServicePrincipal => new AzureServicePrincipalCredentials(),
            AccountType.AzureOidc => new AzureOidcCredentials(),
            AccountType.GoogleCloudAccount => new GcpCredentials(),
            AccountType.AmazonWebServicesOidcAccount => new AwsOidcCredentials(),
            AccountType.OpenClawGateway => new OpenClawGatewayCredentials(),
            _ => null
        };

        return credentials == null
            ? JsonSerializer.SerializeToElement(new object(), JsonOptions)
            : JsonSerializer.SerializeToElement(credentials, credentials.GetType(), JsonOptions);
    }

    private static IReadOnlyDictionary<string, string> BuildSafeEndpointMetadata(JsonElement? endpoint)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (endpoint is not { ValueKind: JsonValueKind.Object } value)
            return metadata;

        foreach (var property in value.EnumerateObject())
        {
            if (!SafeEndpointMetadataNames.Contains(property.Name))
                continue;

            var propertyValue = JsonScalarToString(property.Value);
            if (string.IsNullOrWhiteSpace(propertyValue))
                continue;

            metadata[property.Name] = OctopusImportRedaction.RedactMetadataValue(property.Name, propertyValue);
        }

        return metadata;
    }

    private static string JsonScalarToString(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => bool.TrueString.ToLower(CultureInfo.InvariantCulture),
            JsonValueKind.False => bool.FalseString.ToLower(CultureInfo.InvariantCulture),
            _ => null
        };

    private static string Normalize(string value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}

public sealed record OctopusImportAccountShellMappingResult(
    CreateDeploymentAccountCommand CreateCommand,
    IReadOnlyList<OctopusImportDiagnosticDto> Diagnostics,
    IReadOnlyList<OctopusImportManualConfigurationMarker> ManualConfigurationMarkers)
{
    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
}

public sealed record OctopusImportCertificateShell(
    string Name,
    string Notes,
    bool SourceHasPrivateKey,
    DateTimeOffset? NotBefore,
    DateTimeOffset? NotAfter,
    bool CannotExecuteUntilConfigured);

public sealed record OctopusImportCertificateShellMappingResult(
    OctopusImportCertificateShell Shell,
    IReadOnlyList<OctopusImportDiagnosticDto> Diagnostics,
    IReadOnlyList<OctopusImportManualConfigurationMarker> ManualConfigurationMarkers)
{
    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
}

public sealed record OctopusImportTargetShell(
    string Name,
    bool SourceIsDisabled,
    IReadOnlyList<string> Roles,
    IReadOnlyList<int> EnvironmentIds,
    string CommunicationStyle,
    IReadOnlyDictionary<string, string> EndpointMetadata,
    bool CannotExecuteUntilConfigured);

public sealed record OctopusImportTargetShellMappingResult(
    OctopusImportTargetShell Shell,
    IReadOnlyList<OctopusImportDiagnosticDto> Diagnostics,
    IReadOnlyList<OctopusImportManualConfigurationMarker> ManualConfigurationMarkers)
{
    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
}
