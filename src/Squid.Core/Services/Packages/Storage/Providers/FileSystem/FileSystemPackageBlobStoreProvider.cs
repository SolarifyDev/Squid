using Squid.Core.Settings.Setup;
using Squid.Message.Enums.PackageStorage;
using Squid.Message.Models.PackageStorage;

namespace Squid.Core.Services.Packages.Storage.Providers.FileSystem;

public sealed class FileSystemPackageBlobStoreProvider(DataDirectorySetting dataDirectory) : IPackageBlobStoreProvider, IPackageBlobStoreWarnings, ISingletonDependency
{
    /// <summary>The key of the one setting. Saved stores refer to it: never rename it.</summary>
    public const string RootKey = "Root";

    public string Kind => FileSystemPackageBlobStore.StoreKind;

    public PackageStoreProviderDescriptor Describe()
    {
        return new PackageStoreProviderDescriptor
        {
            Kind = Kind,
            DisplayName = "Local disk",
            Description = "Keeps packages as files in a folder on the server.",
            Fields =
            [
                new PackageStoreFieldDescriptor
                {
                    Key = RootKey,
                    Label = "Root folder",
                    Kind = PackageStoreFieldKind.Path,
                    Required = false,
                    Placeholder = "Leave empty to use the data directory",
                    HelpText = "Leave empty to keep packages in the \"packages\" folder of the data directory (or beside the application when there is none). In a container the folder must be on a mounted volume, or packages are lost when the container is recreated."
                }
            ]
        };
    }

    public IPackageBlobStore Create(PackageBlobStoreConfiguration configuration) => new FileSystemPackageBlobStore(ResolveRoot(configuration));

    public async Task<PackageBlobStoreTestResult> TestAsync(PackageBlobStoreConfiguration configuration, CancellationToken ct)
    {
        var root = ResolveRoot(configuration);
        var cleanup = FileSystemProbeCleanup.Begin(root);
        var result = await PackageBlobStoreProbe.RunAsync(new FileSystemPackageBlobStore(root), ct).ConfigureAwait(false);

        cleanup.Finish();

        result.Message = DescribeOutcome(result, cleanup.RootExisted, root);

        return result;
    }

    // A root that did not exist is no failure (it is created with the first package), but "Storage OK" would hide that a mistyped path looks the same.
    private static string DescribeOutcome(PackageBlobStoreTestResult result, bool rootExisted, string root)
    {
        if (rootExisted || !result.Success) return $"{result.Message} Folder: {root}";

        return $"Folder does not exist yet; it will be created when the first package is stored. A probe file could be written and removed there. Folder: {root}";
    }

    public IReadOnlyList<string> GetWarnings(PackageBlobStoreConfiguration configuration)
    {
        var root = ResolveRoot(configuration);

        return new[] { FileSystemRootResolver.FindApplicationFolderWarning(root, AppContext.BaseDirectory), FileSystemRootPersistenceInspector.FindWarning(root) }.Where(w => w != null).ToList();
    }

    private string ResolveRoot(PackageBlobStoreConfiguration configuration)
    {
        return FileSystemRootResolver.Resolve(configuration.Property(RootKey), dataDirectory.IsConfigured ? dataDirectory.Value : null, AppContext.BaseDirectory);
    }
}
