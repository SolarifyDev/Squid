using System.IO;
using System.Linq;
using Autofac;
using Microsoft.Extensions.Configuration;
using Serilog;
using Squid.Core;
using Squid.Core.Services.Packages.Storage;
using Squid.UnitTests.Services.Packages.Storage.Support;

namespace Squid.UnitTests.Setup;

/// <summary>
/// <see cref="PackageStoreProviderCatalog"/> is a hand-kept second list of providers (the setup host has no container). A provider registered only
/// in DI would silently be missing from the first-run wizard, so the two must agree.
/// </summary>
public sealed class PackageStoreProviderCatalogDriftTests
{
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["DataDirectory"] = Path.Combine(Path.GetTempPath(), "squid-drift") }).Build();

    private static IContainer BuildServerContainer(IConfiguration configuration, Action<ContainerBuilder> extra = null)
    {
        var builder = new ContainerBuilder();

        builder.RegisterInstance(configuration).As<IConfiguration>().SingleInstance();
        builder.RegisterModule(new SquidModule(new LoggerConfiguration().CreateLogger(), configuration));
        extra?.Invoke(builder);

        return builder.Build();
    }

    private static void EnsureCatalogMatchesDi(IContainer container, IConfiguration configuration)
    {
        var registered = container.Resolve<IEnumerable<IPackageBlobStoreProvider>>().Select(p => p.Kind).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var cataloged = PackageStoreProviderCatalog.Create(configuration).Select(p => p.Kind).OrderBy(k => k, StringComparer.Ordinal).ToList();

        registered.ShouldBe(cataloged, $"Package store providers registered in DI: [{string.Join(", ", registered)}]; in PackageStoreProviderCatalog: [{string.Join(", ", cataloged)}]. A provider missing from the catalog is missing from the first-run setup wizard: add it to PackageStoreProviderCatalog.Create.");
    }

    [Fact]
    public void ProvidersInTheServerContainer_AreExactlyThoseInTheSetupCatalog()
    {
        var configuration = Configuration();

        using var container = BuildServerContainer(configuration);

        EnsureCatalogMatchesDi(container, configuration);
    }

    [Fact]
    public void ProviderRegisteredOnlyInDi_IsDetected_WithAnActionableMessage()
    {
        var configuration = Configuration();

        using var container = BuildServerContainer(configuration, b => b.RegisterType<FakeSecretBlobStoreProvider>().As<IPackageBlobStoreProvider>().SingleInstance());

        var ex = Should.Throw<ShouldAssertException>(() => EnsureCatalogMatchesDi(container, configuration));

        ex.Message.ShouldContain("add it to PackageStoreProviderCatalog.Create");
        ex.Message.ShouldContain(FakeSecretBlobStoreProvider.FakeKind);
    }
}
