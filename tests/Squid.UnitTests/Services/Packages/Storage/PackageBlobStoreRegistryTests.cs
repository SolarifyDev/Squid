using System.IO;
using System.Linq;
using Autofac;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.UnitTests.Services.Packages.Storage.Support;

namespace Squid.UnitTests.Services.Packages.Storage;

public sealed class PackageBlobStoreRegistryTests : IDisposable
{
    private readonly FakePackageStoreDataProvider _table = new();
    private readonly FakeSecretBlobStoreProvider _provider = new();
    private readonly IContainer _container;
    private readonly PackageBlobStoreRegistry _registry;

    public PackageBlobStoreRegistryTests()
    {
        var builder = new ContainerBuilder();

        builder.RegisterInstance<IPackageStoreDataProvider>(_table);
        _container = builder.Build();

        _registry = new PackageBlobStoreRegistry(_container, [_provider]);
    }

    public void Dispose() => _container.Dispose();

    [Fact]
    public async Task Resolve_BuildsTheStoreFromTheSavedSettings_AndSecrets()
    {
        var saved = _table.Seed("Remote", FakeSecretBlobStoreProvider.FakeKind, "{\"Endpoint\":\"https://x\"}", "{\"Token\":\"s3cret\"}");

        var store = await _registry.ResolveAsync(saved.Id, CancellationToken.None);

        store.Kind.ShouldBe(FakeSecretBlobStoreProvider.FakeKind);
        _provider.LastConfiguration.Property("Endpoint").ShouldBe("https://x");
        _provider.LastConfiguration.Secret("Token").ShouldBe("s3cret");
    }

    [Fact]
    public async Task Resolve_ReturnsTheSameInstance_UntilTheRecordIsEdited()
    {
        var saved = _table.Seed("Remote", FakeSecretBlobStoreProvider.FakeKind);

        var first = await _registry.ResolveAsync(saved.Id, CancellationToken.None);
        var second = await _registry.ResolveAsync(saved.Id, CancellationToken.None);

        second.ShouldBeSameAs(first);
        _provider.Created.ShouldBe(1);

        _table.Touch(saved.Id);

        var third = await _registry.ResolveAsync(saved.Id, CancellationToken.None);

        third.ShouldNotBeSameAs(first, "an edited record has a newer LastModifiedDate and must yield a fresh store");
        _provider.Created.ShouldBe(2);
        (await _registry.ResolveAsync(saved.Id, CancellationToken.None)).ShouldBeSameAs(third);
    }

    [Fact]
    public async Task Resolve_KeepsADifferentInstancePerStore()
    {
        var a = _table.Seed("A", FakeSecretBlobStoreProvider.FakeKind);
        var b = _table.Seed("B", FakeSecretBlobStoreProvider.FakeKind);

        (await _registry.ResolveAsync(a.Id, CancellationToken.None)).ShouldNotBeSameAs(await _registry.ResolveAsync(b.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveDefault_FollowsTheDefaultFlag()
    {
        var a = _table.Seed("A", FakeSecretBlobStoreProvider.FakeKind, isDefault: true);
        var b = _table.Seed("B", FakeSecretBlobStoreProvider.FakeKind);

        var first = await _registry.ResolveDefaultAsync(CancellationToken.None);

        first.ShouldBeSameAs(await _registry.ResolveAsync(a.Id, CancellationToken.None));

        await _table.SetDefaultAsync(b.Id, CancellationToken.None);

        (await _registry.ResolveDefaultAsync(CancellationToken.None)).ShouldBeSameAs(await _registry.ResolveAsync(b.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Resolve_OfAnUnknownKind_ThrowsProviderNotFound()
    {
        var saved = _table.Seed("Odd", "Quantum");

        var ex = await Should.ThrowAsync<PackageStoreProviderNotFoundException>(() => _registry.ResolveAsync(saved.Id, CancellationToken.None));

        ex.Kind.ShouldBe("Quantum");
    }

    [Fact]
    public async Task Resolve_OfAnUnknownId_ThrowsNotFound()
    {
        var ex = await Should.ThrowAsync<PackageStoreNotFoundException>(() => _registry.ResolveAsync(404, CancellationToken.None));

        ex.StoreId.ShouldBe(404);
    }

    [Fact]
    public async Task ResolveDefault_WithNoDefault_Throws()
    {
        _table.Seed("A", FakeSecretBlobStoreProvider.FakeKind);

        await Should.ThrowAsync<NoDefaultPackageStoreException>(() => _registry.ResolveDefaultAsync(CancellationToken.None));
    }
}
