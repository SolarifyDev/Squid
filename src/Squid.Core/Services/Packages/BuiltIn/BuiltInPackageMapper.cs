using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.BuiltInPackages;

namespace Squid.Core.Services.Packages.BuiltIn;

public static class BuiltInPackageMapper
{
    public static BuiltInPackageVersionDto ToDto(BuiltInPackage package)
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
            Source = package.Source
        };
    }
}
