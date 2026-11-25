using Squid.Core.Services.Packages.BuiltIn;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing;

public interface IPackageInspectionService : IScopedDependency
{
    /// <summary>Runs every inspector over the archive at <paramref name="path"/>. Never throws for a bad package or a bad inspector: a failure becomes a fact.</summary>
    Task<PackageFacts> InspectAsync(string path, BuiltInPackageFormat format, CancellationToken ct);
}
