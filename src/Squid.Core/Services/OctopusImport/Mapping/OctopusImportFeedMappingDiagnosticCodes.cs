using Squid.Core.Services.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping;

public static class OctopusImportFeedMappingDiagnosticCodes
{
    public const string CredentialsOmitted = OctopusImportRedactionDiagnosticCodes.FeedCredentialsOmitted;
    public const string UnsupportedFeedType = "octopus.mapping.feed.unsupported_feed_type";
}
