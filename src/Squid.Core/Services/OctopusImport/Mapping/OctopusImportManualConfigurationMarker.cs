using System.Text.Json;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping;

public sealed record OctopusImportManualConfigurationMarker(
    string ResourceType,
    string SourceId,
    string SourceName,
    string FieldName,
    string ReasonCode,
    bool IsRequired,
    IReadOnlyDictionary<string, string> Metadata);

public static class OctopusImportManualConfiguration
{
    public const string RequiredPropertyName = "Squid.Import.ManualConfiguration.Required";
    public const string FieldsPropertyName = "Squid.Import.ManualConfiguration.Fields";
    public const string ReasonsPropertyName = "Squid.Import.ManualConfiguration.Reasons";
    public const string SourceIdPropertyName = "Squid.Import.Octopus.SourceId";
    public const string SourceTypePropertyName = "Squid.Import.Octopus.SourceType";
    public const string SourceNamePropertyName = "Squid.Import.Octopus.SourceName";

    public const string FeedCredentialsField = "Credentials";
    public const string AccountCredentialsField = "Credentials";
    public const string CertificatePrivateMaterialField = "CertificatePrivateMaterial";
    public const string EndpointSecretsField = "EndpointSecrets";

    public static OctopusImportManualConfigurationMarker CreateMarker(
        OctopusResourceNode resource,
        string fieldName,
        string reasonCode,
        bool isRequired = true,
        IReadOnlyDictionary<string, string> metadata = null)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return new OctopusImportManualConfigurationMarker(
            resource.Kind.ToString(),
            OctopusImportRedaction.RedactMetadataValue("SourceId", resource.SourceId),
            OctopusImportRedaction.RedactMetadataValue("SourceName", resource.Name),
            fieldName?.Trim() ?? string.Empty,
            reasonCode,
            isRequired,
            OctopusImportRedaction.RedactProperties(metadata ?? new Dictionary<string, string>()));
    }

    public static void AddMarkerProperties(
        Dictionary<string, string> properties,
        OctopusResourceNode resource,
        IReadOnlyList<OctopusImportManualConfigurationMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(resource);

        if (markers == null || markers.Count == 0)
            return;

        properties[RequiredPropertyName] = markers.Any(m => m.IsRequired).ToString().ToLowerInvariant();
        properties[FieldsPropertyName] = string.Join(",", markers.Select(m => m.FieldName).Distinct(StringComparer.OrdinalIgnoreCase));
        properties[ReasonsPropertyName] = string.Join(",", markers.Select(m => m.ReasonCode).Distinct(StringComparer.OrdinalIgnoreCase));
        properties[SourceIdPropertyName] = OctopusImportRedaction.RedactMetadataValue(SourceIdPropertyName, resource.SourceId);
        properties[SourceTypePropertyName] = resource.Kind.ToString();
        properties[SourceNamePropertyName] = OctopusImportRedaction.RedactMetadataValue(SourceNamePropertyName, resource.Name);
    }

    public static OctopusImportDiagnosticDto Diagnostic(
        OctopusImportCompatibilitySeverity severity,
        string code,
        string message,
        OctopusResourceNode resource)
        => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = severity,
            Code = code,
            Message = message,
            ResourceType = resource.Kind.ToString(),
            SourceId = resource.SourceId,
            ResourceName = resource.Name
        });

    public static IReadOnlyList<OctopusImportManualConfigurationMarker> BuildRequiredMarkers(OctopusResourceNode resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return resource.Kind switch
        {
            OctopusResourceKind.Feed when FeedHasCredentials(resource.GetSource<OctopusFeedDto>()) =>
            [
                CreateMarker(resource, FeedCredentialsField, OctopusImportRedactionDiagnosticCodes.FeedCredentialsOmitted)
            ],
            OctopusResourceKind.Account when HasJsonPayload(resource.GetSource<OctopusAccountDto>()?.Credentials) =>
            [
                CreateMarker(resource, AccountCredentialsField, OctopusImportRedactionDiagnosticCodes.AccountCredentialsOmitted)
            ],
            OctopusResourceKind.Certificate when CertificateHasPrivateMaterial(resource.GetSource<OctopusCertificateDto>()) =>
            [
                CreateMarker(resource, CertificatePrivateMaterialField, OctopusImportRedactionDiagnosticCodes.CertificatePrivateMaterialOmitted)
            ],
            OctopusResourceKind.Machine when HasEndpointSecret(resource.GetSource<OctopusMachineDto>()?.Endpoint) =>
            [
                CreateMarker(resource, EndpointSecretsField, OctopusImportRedactionDiagnosticCodes.EndpointSecretOmitted)
            ],
            _ => []
        };
    }

    public static IReadOnlyList<OctopusImportDiagnosticDto> BuildRequiredConfigurationDiagnostics(OctopusResourceNode resource)
        => BuildRequiredMarkers(resource)
            .Select(marker => Diagnostic(
                OctopusImportCompatibilitySeverity.Warning,
                marker.ReasonCode,
                MessageFor(marker),
                resource))
            .ToList();

    public static bool HasJsonPayload(JsonElement? element)
        => element is { } value && HasJsonPayload(value);

    public static bool HasEndpointSecret(JsonElement? endpoint)
        => endpoint is { } value && HasEndpointSecret(value, null);

    private static bool FeedHasCredentials(OctopusFeedDto feed)
        => !string.IsNullOrWhiteSpace(feed?.Username) || !string.IsNullOrWhiteSpace(feed?.Password);

    private static bool CertificateHasPrivateMaterial(OctopusCertificateDto certificate)
        => certificate is { HasPrivateKey: true } || HasJsonPayload(certificate?.CertificateData);

    private static bool HasJsonPayload(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => false,
            JsonValueKind.Object => element.EnumerateObject().Any(),
            JsonValueKind.Array => element.EnumerateArray().Any(),
            JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString()),
            _ => true
        };

    private static bool HasEndpointSecret(JsonElement element, string propertyName)
    {
        if (!string.IsNullOrWhiteSpace(propertyName)
            && (IsSensitiveEndpointContainerName(propertyName)
                || OctopusImportRedaction.ShouldRedactPropertyValue(propertyName)))
            return true;

        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Any(property => HasEndpointSecret(property.Value, property.Name)),
            JsonValueKind.Array => element.EnumerateArray().Any(child => HasEndpointSecret(child, propertyName)),
            JsonValueKind.String => OctopusImportRedaction.ShouldRedactPropertyValue(propertyName, element.GetString()),
            _ => false
        };
    }

    private static bool IsSensitiveEndpointContainerName(string propertyName)
        => string.Equals(propertyName, "SubscriptionId", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "ProviderConfig", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "Credentials", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "Credential", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "CertificateData", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "ClientCertificateData", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "ClientCertificateKeyData", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "PrivateKey", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "PrivateKeyFile", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "PrivateKeyPassphrase", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "ProxyPassword", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "InlineGatewayToken", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "InlineHooksToken", StringComparison.OrdinalIgnoreCase);

    private static string MessageFor(OctopusImportManualConfigurationMarker marker)
        => marker.ReasonCode switch
        {
            OctopusImportRedactionDiagnosticCodes.FeedCredentialsOmitted =>
                "Octopus feed credentials were omitted from the import shell and must be configured manually after import.",
            OctopusImportRedactionDiagnosticCodes.AccountCredentialsOmitted =>
                "Octopus account credentials were omitted from the import shell and must be configured manually after import.",
            OctopusImportRedactionDiagnosticCodes.CertificatePrivateMaterialOmitted =>
                "Octopus certificate private material was omitted and the certificate must be recreated manually after import.",
            OctopusImportRedactionDiagnosticCodes.EndpointSecretOmitted =>
                "Octopus target endpoint secrets were omitted and the deployment target must be configured manually after import.",
            _ => "Octopus source data was omitted and must be configured manually after import."
        };
}
