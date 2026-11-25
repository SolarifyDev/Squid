using Squid.Message.Commands.PackageStorage;
using Squid.Message.Models.PackageStorage;

namespace Squid.Core.Services.Packages.Storage;

public interface IPackageStoreService : IScopedDependency
{
    Task<List<PackageStoreDto>> ListAsync(CancellationToken ct);

    Task<PackageStoreDto> GetAsync(int id, CancellationToken ct);

    Task<PackageStoreDto> CreateAsync(CreatePackageStoreCommand command, CancellationToken ct);

    Task<PackageStoreDto> UpdateAsync(UpdatePackageStoreCommand command, CancellationToken ct);

    Task DeleteAsync(int id, CancellationToken ct);

    Task<PackageStoreDto> SetDefaultAsync(int id, CancellationToken ct);

    /// <summary>Runs the provider's check against the saved settings and records the outcome on the store.</summary>
    Task<PackageBlobStoreTestResult> TestAsync(int id, CancellationToken ct);

    List<PackageStoreProviderDescriptor> DescribeProviders();
}
