using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Publishing;
using Squid.Message.Models.BuiltInPackages;

namespace Squid.Core.Services.Packages.BuiltIn;

public static class BuiltInPackageMapper
{
    /// <summary>Facts carry the whole file list, so they are only included where a single version is asked for, never in a list.</summary>
    public static BuiltInPackageVersionDto ToDto(BuiltInPackage package, bool withFacts = false)
    {
        return new BuiltInPackageVersionDto
        {
            Id = package.Id,
            FeedId = package.FeedId,
            PackageId = package.PackageId,
            Version = package.Version,
            FileExtension = package.FileExtension,
            SizeBytes = package.SizeBytes,
            Sha256 = package.Sha256,
            PublishedDate = package.PublishedDate,
            PublishedBy = package.PublishedBy,
            Source = package.Source,
            Facts = withFacts ? PackageFactsJson.Deserialize(package.Facts) : null
        };
    }
}
