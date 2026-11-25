using System.IO;
using System.Linq;
using Squid.Core.Services.Packages.Storage;
using Squid.Message.Enums.PackageStorage;
using Squid.Message.Models.PackageStorage;

namespace Squid.UnitTests.Services.Packages.Storage.Support;

/// <summary>A provider kind with every field type, so secret handling can be exercised without a real remote store.</summary>
internal sealed class FakeSecretBlobStoreProvider : IPackageBlobStoreProvider
{
    public const string FakeKind = "Fake";

    public int Created { get; private set; }

    public PackageBlobStoreConfiguration LastConfiguration { get; private set; }

    public string Kind => FakeKind;

    public PackageStoreProviderDescriptor Describe()
    {
        return new PackageStoreProviderDescriptor
        {
            Kind = Kind,
            DisplayName = "Fake",
            Description = "test",
            Fields =
            [
                new PackageStoreFieldDescriptor { Key = "Endpoint", Label = "Endpoint", Kind = PackageStoreFieldKind.Text, Required = true },
                new PackageStoreFieldDescriptor { Key = "Mode", Label = "Mode", Kind = PackageStoreFieldKind.Select, Options = ["fast", "safe"] },
                new PackageStoreFieldDescriptor { Key = "Token", Label = "Token", Kind = PackageStoreFieldKind.Secret, Required = true, BoundTo = ["Endpoint", "Mode"] },
                new PackageStoreFieldDescriptor { Key = "Proxy", Label = "Proxy password", Kind = PackageStoreFieldKind.Secret }
            ]
        };
    }

    public List<string> Problems { get; } = new();

    public IReadOnlyList<string> ValidateConfiguration(PackageBlobStoreConfiguration configuration) => Problems;

    public IPackageBlobStore Create(PackageBlobStoreConfiguration configuration)
    {
        Created++;
        LastConfiguration = configuration;

        return new NullBlobStore();
    }

    public Task<PackageBlobStoreTestResult> TestAsync(PackageBlobStoreConfiguration configuration, CancellationToken ct)
    {
        LastConfiguration = configuration;

        return Task.FromResult(new PackageBlobStoreTestResult { Success = true, Message = "fine" });
    }

    internal sealed class NullBlobStore : IPackageBlobStore
    {
        public string Kind => FakeKind;
        public Task PutAsync(string key, Stream content, CancellationToken ct) => Task.CompletedTask;
        public Task<Stream> OpenReadAsync(string key, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream());
        public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(false);
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
        public async IAsyncEnumerable<string> ListKeysAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
