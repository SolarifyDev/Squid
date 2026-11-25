using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Persistence.Entities.PackageStorage;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Core.Services.Packages.Versioning;
using Squid.Message.Constants;
using Squid.Message.Models.BuiltInPackages;
using Squid.Message.Models.PackageVersioning;
using Squid.UnitTests.Services.Packages.Storage.Support;
using Squid.UnitTests.Services.Packages.Versioning;

namespace Squid.UnitTests.Services.Packages.BuiltIn;

/// <summary>Builds the archives the tests push.</summary>
internal static class TestArchives
{
    public static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));

                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    public static byte[] TarGz(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            foreach (var (name, content) in entries)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
        }

        return buffer.ToArray();
    }

    public static byte[] PlainTar(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var tar = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
        }

        return buffer.ToArray();
    }

    public static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>The built-in package index in memory, with the same case-insensitive id and exact version matching as the real one.</summary>
internal sealed class FakeBuiltInPackageDataProvider : IBuiltInPackageDataProvider
{
    private readonly List<BuiltInPackage> _rows = new();
    private readonly Dictionary<(int, string), string> _canonical = new();
    private int _nextId = 1;

    public IReadOnlyList<BuiltInPackage> Rows => _rows;

    public int CanonicalCalls { get; private set; }

    public int Inserts { get; private set; }

    public int Replaces { get; private set; }

    public BuiltInPackage Seed(int feedId, string packageId, string version, string sha256, DateTimeOffset? published = null, int storeId = 1)
    {
        sha256 = sha256.PadRight(64, '0');

        var row = new BuiltInPackage { Id = _nextId++, FeedId = feedId, PackageId = packageId, Version = version, Sha256 = sha256, FileExtension = ".zip", StoreId = storeId, BlobKey = BuiltInBlobKey.For(sha256, ".zip"), SizeBytes = 10, PublishedDate = published ?? DateTimeOffset.UtcNow, Source = "api" };

        _rows.Add(row);
        _canonical.TryAdd((feedId, packageId.ToLowerInvariant()), packageId);

        return row;
    }

    public Task<string> ResolveCanonicalIdAsync(int feedId, string packageId, CancellationToken ct)
    {
        CanonicalCalls++;

        _canonical.TryAdd((feedId, packageId.ToLowerInvariant()), packageId);

        return Task.FromResult(_canonical[(feedId, packageId.ToLowerInvariant())]);
    }

    public Task<BuiltInPackage> GetAsync(int feedId, string packageId, string version, CancellationToken ct)
    {
        return Task.FromResult(_rows.FirstOrDefault(r => r.FeedId == feedId && string.Equals(r.PackageId, packageId, StringComparison.OrdinalIgnoreCase) && r.Version == version));
    }

    public Task<List<BuiltInPackage>> ListVersionsAsync(int feedId, string packageId, CancellationToken ct)
    {
        return Task.FromResult(_rows.Where(r => r.FeedId == feedId && string.Equals(r.PackageId, packageId, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.PublishedDate).ToList());
    }

    public Task<List<BuiltInPackageDto>> ListPackagesAsync(int feedId, string filter, int take, CancellationToken ct)
    {
        var matching = _rows.Where(r => r.FeedId == feedId && r.PackageId.Contains(filter ?? string.Empty, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(matching.GroupBy(r => r.PackageId).Select(g => new BuiltInPackageDto { PackageId = g.Key, VersionCount = g.Count(), LastPublishedDate = g.Max(r => r.PublishedDate) }).OrderBy(p => p.PackageId).Take(take).ToList());
    }

    public Task InsertAsync(BuiltInPackage package, CancellationToken ct)
    {
        Inserts++;

        if (_rows.Any(r => r.FeedId == package.FeedId && string.Equals(r.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase) && r.Version == package.Version))
            throw new PackageVersionAlreadyExistsException(package.PackageId, package.Version);

        package.Id = _nextId++;
        _rows.Add(package);

        return Task.CompletedTask;
    }

    public Task ReplaceContentAsync(int id, BuiltInPackage content, CancellationToken ct)
    {
        Replaces++;

        var row = _rows.Single(r => r.Id == id);

        row.FileExtension = content.FileExtension;
        row.SizeBytes = content.SizeBytes;
        row.Sha256 = content.Sha256;
        row.StoreId = content.StoreId;
        row.BlobKey = content.BlobKey;
        row.Source = content.Source;

        return Task.CompletedTask;
    }
}

internal sealed class InMemoryBlobStore : IPackageBlobStore
{
    public Dictionary<string, byte[]> Blobs { get; } = new();

    public int Puts { get; private set; }

    public string Kind => "Memory";

    public Task PutAsync(string key, Stream content, CancellationToken ct)
    {
        Puts++;

        using var copy = new MemoryStream();

        content.CopyTo(copy);
        Blobs[key] = copy.ToArray();

        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        return Blobs.TryGetValue(key, out var bytes) ? Task.FromResult<Stream>(new MemoryStream(bytes)) : throw new PackageBlobNotFoundException(key);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(Blobs.ContainsKey(key));

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        Blobs.Remove(key);

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ListKeysAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var key in Blobs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            yield return key;

        await Task.CompletedTask;
    }
}

internal sealed class FakeBlobStoreRegistry(InMemoryBlobStore store) : IPackageBlobStoreRegistry
{
    public List<int> Resolved { get; } = new();

    public Task<IPackageBlobStore> ResolveAsync(int storeId, CancellationToken ct)
    {
        Resolved.Add(storeId);

        return Task.FromResult<IPackageBlobStore>(store);
    }

    public Task<IPackageBlobStore> ResolveDefaultAsync(CancellationToken ct) => throw new NotSupportedException("the push resolves the default store through its id so the id can be recorded");
}

internal sealed class FakeFeedLocator(ExternalFeed feed) : IBuiltInFeedLocator
{
    public Task<ExternalFeed> GetAsync(int spaceId, CancellationToken ct) => spaceId == feed.SpaceId ? Task.FromResult(feed) : throw new BuiltInFeedNotFoundException(spaceId);
}

internal sealed class FakeUsage : IBuiltInPackageUsage
{
    public HashSet<(string PackageId, string Version)> Selected { get; } = new();

    public Task<bool> IsVersionSelectedByReleaseAsync(int feedId, string packageId, string version, CancellationToken ct)
    {
        return Task.FromResult(Selected.Any(s => string.Equals(s.PackageId, packageId, StringComparison.OrdinalIgnoreCase) && s.Version == version));
    }
}

internal sealed class FakeRules(string template = PackageVersioning.DefaultTemplate) : IPackageVersionRuleService
{
    public List<string> AskedFor { get; } = new();

    public Task<PackageVersionRuleDto> GetEffectiveRuleAsync(int? spaceId, int feedId, string packageId, CancellationToken ct)
    {
        AskedFor.Add(packageId);

        return Task.FromResult(new PackageVersionRuleDto { FeedId = feedId, PackageId = packageId, VersionTemplate = template });
    }

    public Task<PackageVersionRuleDto> SetRuleAsync(Squid.Message.Commands.PackageVersioning.SetPackageVersionRuleCommand command, CancellationToken ct) => throw new NotSupportedException();

    public Task<List<PackageVersionRuleDto>> ListRulesAsync(int? spaceId, int feedId, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>Wraps the real assigner and remembers every call, so a test can say a version was (not) assigned and with which template and package id.</summary>
internal sealed class SpyAssigner(IPackageVersionAssigner inner) : IPackageVersionAssigner
{
    public List<(string Template, VersionTokenContext Context)> Assigns { get; } = new();

    public Task<string> AssignAsync(string template, VersionTokenContext context, CancellationToken ct)
    {
        Assigns.Add((template, context));

        return inner.AssignAsync(template, context, ct);
    }

    public Task<string> PreviewAsync(string template, VersionTokenContext sample, CancellationToken ct) => inner.PreviewAsync(template, sample, ct);
}

/// <summary>Everything a push needs, in memory, wired the way the container wires it.</summary>
internal sealed class PushHarness
{
    public const int SpaceId = 3;

    public ExternalFeed Feed { get; } = new() { Id = 11, SpaceId = SpaceId, FeedType = BuiltInFeed.FeedType, Name = "Built-in feed" };

    public FakeBuiltInPackageDataProvider Packages { get; } = new();

    public InMemoryBlobStore Blobs { get; } = new();

    public FakeBlobStoreRegistry Registry { get; }

    public FakeUsage Usage { get; } = new();

    public FakeRules Rules { get; set; } = new();

    public FakePackageStoreDataProvider Stores { get; } = new();

    public FakeCounterStore Counter { get; } = new();

    public SpyAssigner Assigner { get; }

    public PushHarness()
    {
        Registry = new FakeBlobStoreRegistry(Blobs);
        Assigner = new SpyAssigner(VersioningTestContexts.AssignerWith(Counter, TimeProvider.System));

        Stores.Seed("Local disk", "FileSystem", isDefault: true);
    }

    public BuiltInPackagePushService Service(Squid.Core.Services.Identity.ICurrentUser user = null)
    {
        return new BuiltInPackagePushService(new FakeFeedLocator(Feed), Packages, Usage, Assigner, Rules, Stores, Registry, user, TimeProvider.System);
    }

    public static Squid.Message.Commands.BuiltInPackages.PushBuiltInPackageCommand Push(byte[] bytes, string fileName = "Acme.Web.1.0.0.zip", string packageId = null, string version = null, bool replace = false)
    {
        return new Squid.Message.Commands.BuiltInPackages.PushBuiltInPackageCommand { SpaceId = SpaceId, FileName = fileName, PackageId = packageId, Version = version, Replace = replace, Content = new MemoryStream(bytes) };
    }
}
