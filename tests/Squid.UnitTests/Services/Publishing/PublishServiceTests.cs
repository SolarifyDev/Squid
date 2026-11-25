using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Publishing;
using Squid.Core.Services.Publishing.Exceptions;
using Squid.Core.Services.Publishing.Recipes.Dockerfile;
using Squid.Core.Services.Publishing.Recipes.StaticSite;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Commands.Publishing;
using Squid.Message.Events.Deployments.Deployment;
using Squid.Message.Events.Deployments.Release;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Models.Publishing;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;

namespace Squid.UnitTests.Services.Publishing;

public class PublishServiceTests
{
    private const int SpaceId = 3;
    private const int FeedId = 9;

    private readonly Mock<IBuiltInFeedLocator> _feeds = new();
    private readonly Mock<IBuiltInPackageDataProvider> _packages = new();
    private readonly Mock<IDeploymentPlanApplier> _applier = new();
    private readonly Mock<IReleaseDataProvider> _releaseData = new();
    private readonly Mock<IReleaseService> _releases = new();
    private readonly Mock<IDeploymentService> _deployments = new();
    private readonly List<PlanApplication> _applications = new();
    private readonly List<CreateReleaseCommand> _releaseCommands = new();
    private readonly List<CreateDeploymentCommand> _deploymentCommands = new();
    private AppliedPlan _applied = new() { ProjectId = 7, ChannelId = 70, ProjectCreated = true, PackageActionNames = ["Deploy package"] };

    public PublishServiceTests()
    {
        _feeds.Setup(f => f.GetAsync(SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(new ExternalFeed { Id = FeedId });
        _applier.Setup(a => a.ApplyAsync(It.IsAny<PlanApplication>(), It.IsAny<CancellationToken>())).Callback<PlanApplication, CancellationToken>((a, _) => _applications.Add(a)).ReturnsAsync(() => _applied);
        _releases.Setup(r => r.CreateReleaseAsync(It.IsAny<CreateReleaseCommand>(), It.IsAny<CancellationToken>())).Callback<CreateReleaseCommand, CancellationToken>((c, _) => _releaseCommands.Add(c)).ReturnsAsync(new ReleaseCreatedEvent { Release = new ReleaseDto { Id = 700 } });
        _deployments.Setup(d => d.CreateDeploymentAsync(It.IsAny<CreateDeploymentCommand>(), It.IsAny<CancellationToken>())).Callback<CreateDeploymentCommand, CancellationToken>((c, _) => _deploymentCommands.Add(c)).ReturnsAsync(new DeploymentCreatedEvent { TaskId = 9000, Deployment = new DeploymentDto { Id = 800 } });
    }

    private PublishService Service()
    {
        var registry = new DeployRecipeRegistry([new StaticSiteRecipe(), new DockerfileRecipe()]);

        return new PublishService(_feeds.Object, _packages.Object, registry, _applier.Object, _releaseData.Object, _releases.Object, _deployments.Object);
    }

    private void StoredPackage(PackageFacts facts, string packageId = "Acme.Web", string version = "1.2.3")
    {
        var package = new BuiltInPackage { SpaceId = SpaceId, FeedId = FeedId, PackageId = packageId, Version = version, Facts = facts == null ? null : PackageFactsJson.Serialize(facts) };

        _packages.Setup(p => p.GetAsync(FeedId, packageId, version, It.IsAny<CancellationToken>())).ReturnsAsync(package);
    }

    private static PackageFacts Facts(params (string, string)[] values)
    {
        var facts = new PackageFacts();

        foreach (var (key, value) in values) facts.Values[key] = value;

        return facts;
    }

    private static PackageFacts StaticOnly() => Facts((PackageFactKeys.MarkerIndexHtml, "true"), (PackageFactKeys.MarkerIndexHtmlDir, "."));

    private static PackageFacts StaticAndDockerfile() => Facts((PackageFactKeys.MarkerIndexHtml, "true"), (PackageFactKeys.MarkerIndexHtmlDir, "."), (PackageFactKeys.MarkerDockerfile, "true"), (PackageFactKeys.DockerfileExposePort, "8080"));

    private static PublishPackageCommand Command(string recipe = null, int hostPort = 8080) => new() { SpaceId = SpaceId, PackageId = "Acme.Web", Version = "1.2.3", RecipeKind = recipe, EnvironmentId = 5, TargetRoles = ["web"], HostPort = hostPort, ProjectName = "Acme Web" };

    [Fact]
    public async Task Publish_StaticSite_AppliesTheRenderedPlan_CreatesTheReleaseAndDeploys()
    {
        StoredPackage(StaticOnly());

        var result = await Service().PublishAsync(Command(), CancellationToken.None);

        var application = _applications.Single();

        application.ShouldSatisfyAllConditions(a => a.SpaceId.ShouldBe(SpaceId), a => a.FeedId.ShouldBe(FeedId), a => a.PackageId.ShouldBe("Acme.Web"), a => a.RecipeKind.ShouldBe("static-site"), a => a.ProjectName.ShouldBe("Acme Web"));
        application.Plan.Steps.Count.ShouldBe(2);

        var release = _releaseCommands.Single();

        release.ShouldSatisfyAllConditions(r => r.ProjectId.ShouldBe(7), r => r.ChannelId.ShouldBe(70), r => r.Version.ShouldBe("1.2.3"), r => r.SpaceId.ShouldBe(SpaceId));
        release.SelectedPackages.Single().ShouldSatisfyAllConditions(p => p.ActionName.ShouldBe("Deploy package"), p => p.PackageReferenceName.ShouldBe("Acme.Web"), p => p.Version.ShouldBe("1.2.3"), p => p.FeedId.ShouldBe(FeedId));

        _deploymentCommands.Single().ShouldSatisfyAllConditions(d => d.ReleaseId.ShouldBe(700), d => d.EnvironmentId.ShouldBe(5), d => d.SpaceId.ShouldBe(SpaceId));
        result.ShouldSatisfyAllConditions(r => r.ProjectId.ShouldBe(7), r => r.ReleaseId.ShouldBe(700), r => r.DeploymentId.ShouldBe(800), r => r.TaskId.ShouldBe(9000), r => r.RecipeKind.ShouldBe("static-site"), r => r.ProjectCreated.ShouldBeTrue());
    }

    [Fact]
    public async Task Publish_PicksTheTopScoredRecipe_WhenNoneIsNamed()
    {
        StoredPackage(StaticAndDockerfile());

        (await Service().PublishAsync(Command(), CancellationToken.None)).RecipeKind.ShouldBe("dockerfile");
    }

    [Fact]
    public async Task Publish_AnExplicitKind_BeatsTheRanking_IgnoringCase()
    {
        StoredPackage(StaticAndDockerfile());

        (await Service().PublishAsync(Command("STATIC-site"), CancellationToken.None)).RecipeKind.ShouldBe("static-site");
    }

    [Fact]
    public async Task Publish_SquidYamlNamingARecipe_BeatsTheRanking()
    {
        var facts = StaticAndDockerfile();

        facts.Values[PackageFactKeys.SquidRecipe] = "static-site";
        StoredPackage(facts);

        (await Service().PublishAsync(Command(), CancellationToken.None)).RecipeKind.ShouldBe("static-site");
    }

    [Fact]
    public async Task Publish_AnExplicitKind_BeatsSquidYaml()
    {
        var facts = StaticAndDockerfile();

        facts.Values[PackageFactKeys.SquidRecipe] = "static-site";
        StoredPackage(facts);

        (await Service().PublishAsync(Command("dockerfile"), CancellationToken.None)).RecipeKind.ShouldBe("dockerfile");
    }

    [Fact]
    public async Task Publish_UnknownExplicitKind_ListsTheValidOnes()
    {
        StoredPackage(StaticOnly());

        var ex = await Should.ThrowAsync<PublishRecipeUnknownException>(() => Service().PublishAsync(Command("node"), CancellationToken.None));

        ex.Requested.ShouldBe("node");
        ex.ValidKinds.ShouldBe(["dockerfile", "static-site"]);
        ex.Message.ShouldContain("static-site");
    }

    [Fact]
    public async Task Publish_SquidYamlNamingAnUnknownRecipe_IsReported_NotSilentlyIgnored()
    {
        var facts = StaticOnly();

        facts.Values[PackageFactKeys.SquidRecipe] = "kubernetes";
        StoredPackage(facts);

        var ex = await Should.ThrowAsync<PublishRecipeUnknownException>(() => Service().PublishAsync(Command(), CancellationToken.None));

        ex.Requested.ShouldBe("kubernetes");
    }

    [Fact]
    public async Task Publish_NothingFits_NamesTheRecipesToChooseFrom()
    {
        StoredPackage(Facts((PackageFactKeys.FilesCount, "3")));

        var ex = await Should.ThrowAsync<PublishNoRecipeFitsException>(() => Service().PublishAsync(Command(), CancellationToken.None));

        ex.ValidKinds.ShouldBe(["dockerfile", "static-site"]);
        _applier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Publish_NothingFits_ButARecipeIsNamed_StillWorksWhenItCanRender()
    {
        StoredPackage(Facts((PackageFactKeys.FilesCount, "3")));

        (await Service().PublishAsync(Command("static-site"), CancellationToken.None)).RecipeKind.ShouldBe("static-site");
    }

    [Fact]
    public async Task Publish_PackageNeverInspected_SaysSo()
    {
        StoredPackage(null);

        await Should.ThrowAsync<PublishFactsMissingException>(() => Service().PublishAsync(Command(), CancellationToken.None));
        _applier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Publish_UnknownVersion_IsNotFound()
    {
        await Should.ThrowAsync<BuiltInPackageNotFoundException>(() => Service().PublishAsync(Command(), CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task Publish_MissingOrBadHostPort_IsRefused_BeforeAnythingIsWritten(int port)
    {
        StoredPackage(StaticOnly());

        var ex = await Should.ThrowAsync<RecipeInputInvalidException>(() => Service().PublishAsync(Command(hostPort: port), CancellationToken.None));

        ex.Input.ShouldBe("hostPort");
        _applier.VerifyNoOtherCalls();
        _releases.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Publish_DockerfileWithoutAKnownPort_IsRefused_BeforeAnythingIsWritten()
    {
        StoredPackage(Facts((PackageFactKeys.MarkerDockerfile, "true")));

        var ex = await Should.ThrowAsync<RecipeInputInvalidException>(() => Service().PublishAsync(Command(), CancellationToken.None));

        ex.Input.ShouldBe("containerPort");
        _applier.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Publish_NoTargetRole_IsRefused(string role)
    {
        StoredPackage(StaticOnly());

        var command = Command();

        command.TargetRoles = role == null ? new List<string>() : [role];

        (await Should.ThrowAsync<RecipeInputInvalidException>(() => Service().PublishAsync(command, CancellationToken.None))).Input.ShouldBe("targetRoles");
    }

    [Fact]
    public async Task Publish_RolesAreTrimmedAndDeduplicated_AndACommaIsRefused()
    {
        StoredPackage(StaticOnly());

        var command = Command();

        command.TargetRoles = [" web ", "WEB", "edge"];

        await Service().PublishAsync(command, CancellationToken.None);

        _applications.Single().Plan.Steps[0].TargetRoles.ShouldBe(["web", "edge"]);

        command.TargetRoles = ["a,b"];

        await Should.ThrowAsync<RecipeInputInvalidException>(() => Service().PublishAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task Publish_AVersionPublishedTwice_ReusesItsRelease_WhenTheProcessDidNotChange()
    {
        StoredPackage(StaticOnly());
        _releaseData.Setup(r => r.GetReleaseByVersionAsync(7, 70, "1.2.3", It.IsAny<CancellationToken>())).ReturnsAsync(new ReleaseEntity { Id = 555 });

        var result = await Service().PublishAsync(Command(), CancellationToken.None);

        result.ReleaseId.ShouldBe(555);
        _releases.Verify(r => r.CreateReleaseAsync(It.IsAny<CreateReleaseCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        _deploymentCommands.Single().ReleaseId.ShouldBe(555);
    }

    [Fact]
    public async Task Publish_AVersionPublishedTwice_IsRefused_WhenTheProcessChangedSinceItsRelease()
    {
        StoredPackage(StaticOnly());
        _applied.ProcessUpdated = true;
        _releaseData.Setup(r => r.GetReleaseByVersionAsync(7, 70, "1.2.3", It.IsAny<CancellationToken>())).ReturnsAsync(new ReleaseEntity { Id = 555 });

        await Should.ThrowAsync<PublishReleaseVersionExistsException>(() => Service().PublishAsync(Command(), CancellationToken.None));
        _deployments.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Publish_UsesTheStoredCanonicalPackageIdAndVersion()
    {
        var package = new BuiltInPackage { SpaceId = SpaceId, FeedId = FeedId, PackageId = "Acme.Web", Version = "1.2.3", Facts = PackageFactsJson.Serialize(StaticOnly()) };

        _packages.Setup(p => p.GetAsync(FeedId, "acme.web", "1.2.3", It.IsAny<CancellationToken>())).ReturnsAsync(package);

        var command = Command();

        command.PackageId = "acme.web";

        await Service().PublishAsync(command, CancellationToken.None);

        _applications.Single().PackageId.ShouldBe("Acme.Web");
        _releaseCommands.Single().SelectedPackages.Single().PackageReferenceName.ShouldBe("Acme.Web");
    }
}
