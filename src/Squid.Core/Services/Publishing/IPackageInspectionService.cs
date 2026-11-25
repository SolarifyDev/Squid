using Squid.Core.Services.Packages.BuiltIn;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing;

internal interface IPackageInspectionService : IScopedDependency
{
    /// <summary>A scanner asking for every file the registered inspectors declared. Give it to the walk that reads the archive, then pass what it collected to <see cref="InspectAsync"/>.</summary>
    PackageArchiveScanner CreateScanner();

    /// <summary>
    /// Runs every inspector over what the walk collected, in memory. Never throws for a bad inspector: a failure becomes a fact. The archive at <paramref name="path"/> is
    /// only opened again for a file no inspector declared.
    /// </summary>
    Task<PackageFacts> InspectAsync(string path, BuiltInPackageFormat format, PackageArchiveScan scan, CancellationToken ct);
}
