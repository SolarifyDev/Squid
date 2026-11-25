using System.Linq;
using System.Reflection;
using Autofac;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Squid.Core;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Security;
using Squid.Core.Services.DataSeeding;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.BuiltIn;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageNotes;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageSearch;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Publishing;
using Squid.Core.Services.Publishing.Inspectors;

namespace Squid.UnitTests.Services.Packages.BuiltIn;

/// <summary>
/// Seam tests: each piece of the built-in feed is reached through the real container (the assembly scan <c>SquidModule</c> runs), so one that stops being
/// registered, or is registered twice, makes these go red even though every piece passes its own tests.
/// </summary>
public class BuiltInContainerWiringTests
{
    private static IContainer Build()
    {
        var builder = new ContainerBuilder();
        var module = new SquidModule(Serilog.Core.Logger.None, new ConfigurationBuilder().Build());

        typeof(SquidModule).GetMethod("RegisterDependency", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(module, new object[] { builder });

        // The database edges are mocked: these tests are about what the container can build, not about reading rows.
        builder.RegisterInstance<IConfiguration>(new ConfigurationBuilder().Build());
        builder.RegisterInstance(new Squid.Core.Settings.Setup.DataDirectorySetting(new ConfigurationBuilder().Build()));
        builder.RegisterInstance(new Mock<IRepository>().Object).As<IRepository>();
        builder.RegisterInstance(new Mock<IUnitOfWork>().Object).As<IUnitOfWork>();
        builder.RegisterInstance(new Mock<IAtRestSecretProtector>().Object).As<IAtRestSecretProtector>();
        builder.RegisterInstance(new Mock<ICurrentUser>().Object).As<ICurrentUser>();
        builder.RegisterInstance(new Mock<IHttpContextAccessor>().Object).As<IHttpContextAccessor>();

        return builder.Build();
    }

    [Fact]
    public void BuiltInStrategies_AreRegistered_AndExactlyOneStrategyClaimsTheBuiltInType()
    {
        using var container = Build();
        using var scope = container.BeginLifetimeScope();

        scope.Resolve<IEnumerable<IPackageVersionStrategy>>().Single(s => s.CanHandle("BuiltIn")).ShouldBeOfType<BuiltInPackageVersionStrategy>();
        scope.Resolve<IEnumerable<IPackageSearchStrategy>>().Single(s => s.CanHandle("BuiltIn")).ShouldBeOfType<BuiltInPackageSearchStrategy>();
        scope.Resolve<IEnumerable<IPackageNotesStrategy>>().Single(s => s.CanHandle("BuiltIn")).ShouldBeOfType<BuiltInPackageNotesStrategy>();
    }

    [Fact]
    public void BuiltInPackageSource_IsRegistered_AndTheRouterReachesIt()
    {
        using var container = Build();
        using var scope = container.BeginLifetimeScope();

        scope.Resolve<IEnumerable<IFeedPackageSource>>().Single(s => s.CanHandle("BuiltIn")).ShouldBeOfType<BuiltInFeedPackageSource>();
        scope.Resolve<IEnumerable<IPackageContentFetcher>>().Single().ShouldBeOfType<RoutingPackageContentFetcher>();
    }

    [Fact]
    public void PushPipeline_AndItsEdges_AreRegistered()
    {
        using var container = Build();
        using var scope = container.BeginLifetimeScope();

        scope.Resolve<IBuiltInPackagePushService>().ShouldBeOfType<BuiltInPackagePushService>();
        scope.Resolve<IBuiltInPackageQueryService>().ShouldBeOfType<BuiltInPackageQueryService>();
        scope.Resolve<IBuiltInPackageUsage>().ShouldBeOfType<BuiltInPackageUsage>();
        scope.Resolve<IBuiltInFeedLocator>().ShouldBeOfType<BuiltInFeedService>();
        scope.Resolve<IBuiltInFeedProvisioner>().ShouldBeOfType<BuiltInFeedService>();
    }

    [Fact]
    public void TheBuiltInIndex_IsBothTheDataProvider_AndAPackageStoreUsage()
    {
        using var container = Build();
        using var scope = container.BeginLifetimeScope();

        scope.Resolve<IBuiltInPackageDataProvider>().ShouldBeOfType<BuiltInPackageDataProvider>();
        scope.Resolve<IEnumerable<IPackageStoreUsage>>().Select(u => u.GetType()).ShouldContain(typeof(BuiltInPackageDataProvider), "the store delete guard asks every registered usage; the built-in index must be one");
    }

    [Fact]
    public void TheBuiltInFeedSeeder_IsRegistered_AfterTheDefaultSpaceExists()
    {
        // The module registers every concrete IDataSeeder it finds in the assembly; the runner then runs them by Order.
        var seeders = typeof(SquidModule).Assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && typeof(IDataSeeder).IsAssignableFrom(t)).ToList();

        seeders.ShouldContain(typeof(BuiltInFeedSeeder));
        new BuiltInFeedSeeder().Order.ShouldBeGreaterThan(new DefaultSpaceSeeder().Order, "a space has to exist before it can get its feed");
    }

    [Fact]
    public void Inspectors_AreAllRegistered_AndTheInspectionServiceAndRegistryAreReachable()
    {
        using var container = Build();
        using var scope = container.BeginLifetimeScope();

        scope.Resolve<IEnumerable<IPackageInspector>>().Select(i => i.GetType()).ShouldBe([typeof(FileMarkerInspector), typeof(NodeInspector), typeof(SquidYamlInspector), typeof(DockerfileInspector)], ignoreOrder: true);
        scope.Resolve<IPackageInspectionService>().ShouldBeOfType<PackageInspectionService>();
        scope.Resolve<IDeployRecipeRegistry>().ShouldBeOfType<DeployRecipeRegistry>();
        scope.Resolve<IRecipeRecommendationService>().ShouldBeOfType<RecipeRecommendationService>();
    }

    [Fact]
    public void TheContainersRecipes_HaveUniqueKinds_SoTheRegistryCanBeBuilt()
    {
        using var container = Build();
        using var scope = container.BeginLifetimeScope();

        var kinds = scope.Resolve<IEnumerable<IDeployRecipe>>().Select(r => r.Kind).ToList();

        kinds.Select(k => k.ToLowerInvariant()).Distinct().Count().ShouldBe(kinds.Count, "two recipes of one kind would make squid.yaml's recipe: ambiguous");
        Should.NotThrow(() => scope.Resolve<IDeployRecipeRegistry>());
    }

    [Fact]
    public void Recipes_AreRegistered_AndTheRegistryFindsThemByKind()
    {
        using var container = Build();
        using var scope = container.BeginLifetimeScope();

        scope.Resolve<IEnumerable<IDeployRecipe>>().Select(r => r.Kind).ShouldBe(["static-site", "dockerfile"], ignoreOrder: true);

        var registry = scope.Resolve<IDeployRecipeRegistry>();

        registry.Find("static-site").ShouldBeOfType<Squid.Core.Services.Publishing.Recipes.StaticSite.StaticSiteRecipe>();
        registry.Find("Dockerfile").ShouldBeOfType<Squid.Core.Services.Publishing.Recipes.Dockerfile.DockerfileRecipe>();
        registry.Find("nope").ShouldBeNull();
    }

    [Fact]
    public void PublishPipeline_IsRegistered_AndTheDataProviderIsReachable()
    {
        // The publish service itself needs the whole deployment graph (mapper, job client, settings), which this container leaves out on purpose.
        using var container = Build();
        using var scope = container.BeginLifetimeScope();

        container.IsRegistered<IPublishService>().ShouldBeTrue();
        container.IsRegistered<IDeploymentPlanApplier>().ShouldBeTrue();
        scope.Resolve<IPublishedProjectDataProvider>().ShouldBeOfType<PublishedProjectDataProvider>();
    }

    [Fact]
    public async Task ThePushPipeline_RunsTheContainersInspectors()
    {
        using var container = Build();
        using var scope = container.BeginLifetimeScope();

        var inspection = scope.Resolve<IPackageInspectionService>();
        var path = System.IO.Path.GetTempFileName();

        await System.IO.File.WriteAllBytesAsync(path, TestArchives.Zip(("Dockerfile", "FROM x")));

        try
        {
            var facts = await inspection.InspectAsync(path, BuiltInPackageFormat.Zip, CancellationToken.None);

            facts.Is(Squid.Message.Models.Publishing.PackageFactKeys.MarkerDockerfile).ShouldBeTrue("an inspector the container does not hand to the service would leave the facts empty");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
