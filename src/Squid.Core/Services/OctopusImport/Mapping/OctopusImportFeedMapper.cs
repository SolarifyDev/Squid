using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Commands.Deployments.ExternalFeed;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping;

public interface IOctopusImportFeedMapper : IScopedDependency
{
    OctopusImportFeedMappingResult MapToCreateCommand(
        OctopusResourceNode feedResource,
        int destinationSpaceId);

    OctopusImportFeedMappingResult MapToUpdateCommand(
        OctopusResourceNode feedResource,
        int destinationFeedId,
        int destinationSpaceId);
}

public class OctopusImportFeedMapper : IOctopusImportFeedMapper
{
    public OctopusImportFeedMappingResult MapToCreateCommand(
        OctopusResourceNode feedResource,
        int destinationSpaceId)
    {
        var mapping = Map(feedResource, destinationSpaceId);

        return new OctopusImportFeedMappingResult(
            new CreateExternalFeedCommand
            {
                FeedType = mapping.FeedType,
                Properties = mapping.Properties,
                FeedUri = mapping.FeedUri,
                Username = null,
                Password = null,
                Name = mapping.Name,
                Slug = mapping.Slug,
                PackageAcquisitionLocationOptions = [],
                SpaceId = destinationSpaceId
            },
            null,
            mapping.Diagnostics,
            mapping.ManualConfigurationMarkers);
    }

    public OctopusImportFeedMappingResult MapToUpdateCommand(
        OctopusResourceNode feedResource,
        int destinationFeedId,
        int destinationSpaceId)
    {
        if (destinationFeedId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationFeedId), destinationFeedId, "Destination feed id must be positive.");

        var mapping = Map(feedResource, destinationSpaceId);

        return new OctopusImportFeedMappingResult(
            null,
            new UpdateExternalFeedCommand
            {
                Id = destinationFeedId,
                FeedType = mapping.FeedType,
                Properties = mapping.Properties,
                FeedUri = mapping.FeedUri,
                Username = null,
                PasswordNewValue = null,
                Name = mapping.Name,
                Slug = mapping.Slug,
                PackageAcquisitionLocationOptions = [],
                SpaceId = destinationSpaceId
            },
            mapping.Diagnostics,
            mapping.ManualConfigurationMarkers);
    }

    private static FeedCommandMapping Map(OctopusResourceNode feedResource, int destinationSpaceId)
    {
        ArgumentNullException.ThrowIfNull(feedResource);

        if (feedResource.Kind != OctopusResourceKind.Feed)
            throw new ArgumentException("Octopus feed mapper requires a feed resource.", nameof(feedResource));

        if (destinationSpaceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationSpaceId), destinationSpaceId, "Destination space id must be positive.");

        var feed = feedResource.GetSource<OctopusFeedDto>()
            ?? throw new ArgumentException("Octopus feed resource does not contain an OctopusFeedDto source.", nameof(feedResource));

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        var markers = new List<OctopusImportManualConfigurationMarker>();
        var mappedFeedType = MapFeedType(feed, feedResource, diagnostics);

        if (!string.IsNullOrWhiteSpace(feed.Username) || !string.IsNullOrWhiteSpace(feed.Password))
        {
            markers.Add(OctopusImportManualConfiguration.CreateMarker(
                feedResource,
                OctopusImportManualConfiguration.FeedCredentialsField,
                OctopusImportRedactionDiagnosticCodes.FeedCredentialsOmitted));

            diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Warning,
                OctopusImportFeedMappingDiagnosticCodes.CredentialsOmitted,
                "Octopus feed credentials were omitted from the Squid ExternalFeed command and must be configured manually after import.",
                feedResource));
        }

        var properties = OctopusImportRedaction.RedactProperties(BuildProperties(feed));
        OctopusImportManualConfiguration.AddMarkerProperties(properties, feedResource, markers);

        return new FeedCommandMapping(
            mappedFeedType,
            feed.FeedUri,
            feed.Name,
            feed.Slug,
            properties,
            diagnostics,
            markers);
    }

    private static string MapFeedType(
        OctopusFeedDto feed,
        OctopusResourceNode feedResource,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var feedType = feed.FeedType ?? string.Empty;

        if (ContainsAny(feedType, "Docker", "Container Registry", "OCI Registry", "ECR", "ACR", "GCR"))
            return "Docker Registry";

        if (ContainsAny(feedType, "NuGet"))
            return "NuGet";

        if (ContainsAny(feedType, "Helm"))
            return "Helm";

        if (ContainsAny(feedType, "GitHub") || IsGitHubUri(feed.FeedUri))
            return "GitHub";

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportFeedMappingDiagnosticCodes.UnsupportedFeedType,
            $"Octopus feed type '{feed.FeedType}' is not supported by the current Squid import feed mapper.",
            feedResource));

        return feed.FeedType;
    }

    private static Dictionary<string, string> BuildProperties(OctopusFeedDto feed)
    {
        var properties = new Dictionary<string, string>();

        AddIfPresent(properties, "RegistryPath", feed.RegistryPath);
        AddIfPresent(properties, "ApiVersion", feed.ApiVersion);

        if (feed.DownloadAttempts > 0)
            properties["DownloadAttempts"] = feed.DownloadAttempts.ToString();

        if (feed.DownloadRetryBackoffSeconds > 0)
            properties["DownloadRetryBackoffSeconds"] = feed.DownloadRetryBackoffSeconds.ToString();

        return properties;
    }

    private static void AddIfPresent(Dictionary<string, string> properties, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            properties[key] = value;
    }

    private static bool ContainsAny(string value, params string[] candidates)
        => !string.IsNullOrWhiteSpace(value) &&
           candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static bool IsGitHubUri(string uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
           parsed.Host.Contains("github", StringComparison.OrdinalIgnoreCase);

    private static OctopusImportDiagnosticDto Diagnostic(
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

    private sealed record FeedCommandMapping(
        string FeedType,
        string FeedUri,
        string Name,
        string Slug,
        Dictionary<string, string> Properties,
        IReadOnlyList<OctopusImportDiagnosticDto> Diagnostics,
        IReadOnlyList<OctopusImportManualConfigurationMarker> ManualConfigurationMarkers);
}

public sealed record OctopusImportFeedMappingResult(
    CreateExternalFeedCommand CreateCommand,
    UpdateExternalFeedCommand UpdateCommand,
    IReadOnlyList<OctopusImportDiagnosticDto> Diagnostics,
    IReadOnlyList<OctopusImportManualConfigurationMarker> ManualConfigurationMarkers)
{
    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
}
