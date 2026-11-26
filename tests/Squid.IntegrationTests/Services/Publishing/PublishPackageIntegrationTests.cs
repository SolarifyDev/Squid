using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.Deployments.Variables.Exceptions;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Publishing;
using Squid.Core.Services.Publishing.Exceptions;
using Squid.Message.Commands.BuiltInPackages;
using Squid.Message.Commands.PackageStorage;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Commands.Publishing;
using Squid.Message.Models.Deployments.Variable;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;

namespace Squid.IntegrationTests.Services.Publishing;

/// <summary>
/// Integration tier: the real Postgres schema and the real services end to end (push, inspection, recipe choice, project, process, release, deployment), through
/// the real container. Only the background job client is the test double, so the deployment is created and queued but not run (the pipeline test runs it).
/// </summary>
public class PublishPackageIntegrationTests : TestBase, IDisposable
{
    private const string PackageId = "Acme.Web";
    private const string Role = "web";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"squid-it-publish-{Guid.NewGuid():N}");

    public PublishPackageIntegrationTests() : base("PublishPackage", "squid_it_publish_package")
    {
    }

    void IDisposable.Dispose()
    {
        Dispose();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
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

    private static byte[] StaticSite(string content = "<h1>hi</h1>") => Zip(("index.html", content));

    private static byte[] DockerfileApp(string content = "x") => Zip(("Dockerfile", "FROM busybox\nEXPOSE 8080\n"), ("app.txt", content));

    private sealed record Space(int SpaceId, int EnvironmentId);

    private async Task<Space> SetUpAsync()
    {
        var store = await Run<IPackageStoreService, Squid.Message.Models.PackageStorage.PackageStoreDto>(s => s.CreateAsync(new CreatePackageStoreCommand { Name = "Disk", Kind = "FileSystem", Properties = new Dictionary<string, string> { ["Root"] = _root } }, CancellationToken.None));

        await Run<IPackageStoreService, Squid.Message.Models.PackageStorage.PackageStoreDto>(s => s.SetDefaultAsync(store.Id, CancellationToken.None));

        var slug = $"s{Guid.NewGuid():N}"[..12];
        var spaceId = 0;
        var environmentId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var space = new Squid.Core.Persistence.Entities.Deployments.Space { Name = slug, Slug = slug, Json = "{}" };

            await repository.InsertAsync(space);
            await unitOfWork.SaveChangesAsync();

            spaceId = space.Id;

            await repository.InsertAsync(new ProjectGroup { Name = "Apps", Slug = "apps", Description = "Apps", SpaceId = spaceId });
            var environment = new Environment { Name = "Prod", Slug = "prod", SpaceId = spaceId, SortOrder = 0 };

            await repository.InsertAsync(environment);
            await unitOfWork.SaveChangesAsync();

            environmentId = environment.Id;

            var lifecycle = new Lifecycle { Name = "Default", Slug = "default", SpaceId = spaceId };

            await repository.InsertAsync(lifecycle);
            await unitOfWork.SaveChangesAsync();

            var phase = new LifecyclePhase { LifecycleId = lifecycle.Id, Name = "Prod", SortOrder = 0 };

            await repository.InsertAsync(phase);
            await unitOfWork.SaveChangesAsync();
            await repository.InsertAsync(new LifecyclePhaseEnvironment { PhaseId = phase.Id, EnvironmentId = environmentId, TargetType = Squid.Message.Enums.Deployments.LifecyclePhaseEnvironmentTargetType.Optional });

            await repository.InsertAsync(new Machine { Name = "web-1", Slug = "web-1", IsDisabled = false, Roles = DeploymentTargetFinder.SerializeRoles([Role]), EnvironmentIds = DeploymentTargetFinder.SerializeIds([environmentId]), SpaceId = spaceId, Endpoint = """{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-it","Thumbprint":"IT-THUMBPRINT"}""" });
            await unitOfWork.SaveChangesAsync();
        });

        await Run<IBuiltInFeedProvisioner>(p => p.EnsureAsync(spaceId, CancellationToken.None));

        return new Space(spaceId, environmentId);
    }

    private enum Placement
    {
        FirstPhase,
        SecondPhase
    }

    // Another environment of the same space, in the space's lifecycle (in its first phase, or alone in a second one) and with a machine of the role.
    private async Task<Space> AddEnvironmentAsync(Space space, string name, Placement placement = Placement.FirstPhase)
    {
        var environmentId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var environment = new Environment { Name = name, Slug = name.ToLowerInvariant(), SpaceId = space.SpaceId, SortOrder = 1 };

            await repository.InsertAsync(environment);
            await unitOfWork.SaveChangesAsync();

            environmentId = environment.Id;

            var lifecycle = await repository.QueryNoTracking<Lifecycle>(l => l.SpaceId == space.SpaceId).SingleAsync();
            var phases = await repository.QueryNoTracking<LifecyclePhase>(p => p.LifecycleId == lifecycle.Id).OrderBy(p => p.SortOrder).ToListAsync();
            var phaseId = phases[0].Id;

            if (placement == Placement.SecondPhase)
            {
                var second = new LifecyclePhase { LifecycleId = lifecycle.Id, Name = name, SortOrder = 1 };

                await repository.InsertAsync(second);
                await unitOfWork.SaveChangesAsync();

                phaseId = second.Id;
            }

            await repository.InsertAsync(new LifecyclePhaseEnvironment { PhaseId = phaseId, EnvironmentId = environmentId, TargetType = Squid.Message.Enums.Deployments.LifecyclePhaseEnvironmentTargetType.Optional });
            await repository.InsertAsync(new Machine { Name = $"web-{name}", Slug = $"web-{name.ToLowerInvariant()}", IsDisabled = false, Roles = DeploymentTargetFinder.SerializeRoles([Role]), EnvironmentIds = DeploymentTargetFinder.SerializeIds([environmentId]), SpaceId = space.SpaceId, Endpoint = """{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-it2","Thumbprint":"IT-THUMBPRINT-2"}""" });
            await unitOfWork.SaveChangesAsync();
        });

        return space with { EnvironmentId = environmentId };
    }

    private Task<List<(string Name, string Value, string Scope)>> InputVariablesAsync(int projectId)
    {
        return QueryAsync(async r =>
        {
            var variableSetId = (await r.QueryNoTracking<Project>(p => p.Id == projectId).SingleAsync()).VariableSetId;
            var rows = await (from v in r.QueryNoTracking<Variable>(x => x.VariableSetId == variableSetId && x.Name.StartsWith("Squid.Publish.Input."))
                              join s in r.QueryNoTracking<VariableScope>() on v.Id equals s.VariableId
                              orderby s.ScopeValue
                              select new { v.Name, v.Value, s.ScopeValue }).ToListAsync();

            return rows.Select(x => (x.Name, x.Value, x.ScopeValue)).ToList();
        });
    }

    private async Task<int> CountAsync<T>() where T : class, IEntity => await QueryAsync(r => r.QueryNoTracking<T>().CountAsync());

    private Task PushAsync(int spaceId, byte[] bytes, string version)
    {
        var command = new PushBuiltInPackageCommand { SpaceId = spaceId, FileName = $"{PackageId}.{version}.zip", Version = version, Content = new MemoryStream(bytes), DeclaredSizeBytes = bytes.Length };

        return Run<IBuiltInPackagePushService, Squid.Message.Commands.BuiltInPackages.PushBuiltInPackageResponseData>(push => push.PushAsync(command, CancellationToken.None));
    }

    private Task<PublishPackageResponseData> PublishAsync(Space space, string version, string recipe = null, int port = 8080, string name = null, string role = Role, int? lifecycleId = null)
    {
        var command = new PublishPackageCommand { SpaceId = space.SpaceId, PackageId = PackageId, Version = version, RecipeKind = recipe, EnvironmentId = space.EnvironmentId, TargetRoles = [role], Inputs = new() { ["hostPort"] = port.ToString() }, ProjectName = name, LifecycleId = lifecycleId };

        return Run<IPublishService, PublishPackageResponseData>(publish => publish.PublishAsync(command, CancellationToken.None));
    }

    private async Task<T> QueryAsync<T>(Func<IRepository, Task<T>> query) => await Run<IRepository, T>(query);

    private Task<List<DeploymentStep>> StepsAsync(int processId) => QueryAsync(r => r.QueryNoTracking<DeploymentStep>(s => s.ProcessId == processId).OrderBy(s => s.StepOrder).ToListAsync());

    private Task<Project> ProjectAsync(int id) => QueryAsync(r => r.QueryNoTracking<Project>(p => p.Id == id).SingleAsync());

    [Fact]
    public async Task PublishingAPushedStaticSite_CreatesProjectProcessReleaseAndDeployment()
    {
        var space = await SetUpAsync();

        await PushAsync(space.SpaceId, StaticSite(), "1.0.0");

        var result = await PublishAsync(space, "1.0.0", name: "Acme Site");

        result.ShouldSatisfyAllConditions(r => r.ProjectCreated.ShouldBeTrue(), r => r.ProcessUpdated.ShouldBeFalse(), r => r.RecipeKind.ShouldBe("static-site"), r => r.ReleaseId.ShouldBeGreaterThan(0), r => r.DeploymentId.ShouldBeGreaterThan(0), r => r.TaskId.ShouldBeGreaterThan(0));

        var project = await ProjectAsync(result.ProjectId);

        project.Name.ShouldBe("Acme Site");
        PublishedProjectMarker.TryParse(project.Json).ShouldNotBeNull().PackageId.ShouldBe(PackageId);

        var steps = await StepsAsync(project.DeploymentProcessId);

        steps.Select(s => s.Name).ShouldBe(["Deploy package", "Serve site"]);

        var actions = await QueryAsync(r => r.QueryNoTracking<DeploymentAction>(a => steps.Select(s => s.Id).Contains(a.StepId)).OrderBy(a => a.StepId).ToListAsync());
        var properties = await QueryAsync(r => r.QueryNoTracking<DeploymentActionProperty>(p => actions.Select(a => a.Id).Contains(p.ActionId)).ToListAsync());
        var stepProperties = await QueryAsync(r => r.QueryNoTracking<DeploymentStepProperty>(p => steps.Select(s => s.Id).Contains(p.StepId)).ToListAsync());

        actions.Select(a => a.ActionType).ShouldBe(["Squid.TentaclePackage", "Squid.Script"]);
        stepProperties.ShouldAllBe(p => p.PropertyName == "Squid.Action.TargetRoles" && p.PropertyValue == Role);
        properties.Where(p => p.ActionId == actions[0].Id).ShouldContain(p => p.PropertyName == "Squid.Action.Package.PackageId" && p.PropertyValue == PackageId);
        var script = properties.Single(p => p.ActionId == actions[1].Id && p.PropertyName == "Squid.Action.Script.ScriptBody").PropertyValue;

        script.ShouldContain("Squid.Publish.Input.hostPort | ToBase64");
        script.ShouldNotContain("8080", Case.Sensitive, "the port is not in the shared process; it is a variable of the environment");
        (await InputVariablesAsync(project.Id)).ShouldBe([("Squid.Publish.Input.hostPort", "8080", space.EnvironmentId.ToString())]);

        var release = await QueryAsync(r => r.QueryNoTracking<ReleaseEntity>(x => x.Id == result.ReleaseId).SingleAsync());
        var selected = await QueryAsync(r => r.QueryNoTracking<ReleaseSelectedPackage>(p => p.ReleaseId == release.Id).ToListAsync());
        var feedId = (await Run<IBuiltInFeedLocator, ExternalFeed>(l => l.GetAsync(space.SpaceId, CancellationToken.None))).Id;

        release.ProjectId.ShouldBe(project.Id);
        release.Version.ShouldBe("1.0.0");
        selected.Single().ShouldSatisfyAllConditions(p => p.ActionName.ShouldBe("Deploy package"), p => p.PackageReferenceName.ShouldBe(PackageId), p => p.Version.ShouldBe("1.0.0"), p => p.FeedId.ShouldBe(feedId));

        var deployment = await QueryAsync(r => r.QueryNoTracking<Deployment>(d => d.Id == result.DeploymentId).SingleAsync());
        var task = await QueryAsync(r => r.QueryNoTracking<ServerTask>(t => t.Id == result.TaskId).SingleAsync());

        deployment.ShouldSatisfyAllConditions(d => d.ReleaseId.ShouldBe(release.Id), d => d.EnvironmentId.ShouldBe(space.EnvironmentId), d => d.TaskId.ShouldBe(task.Id));
        task.ProjectId.ShouldBe(project.Id);
    }

    [Fact]
    public async Task PublishingASecondVersion_ReusesTheProject_AndTheProcessIsUntouched()
    {
        var space = await SetUpAsync();

        await PushAsync(space.SpaceId, StaticSite("one"), "1.0.0");
        await PushAsync(space.SpaceId, StaticSite("two"), "1.0.1");

        var first = await PublishAsync(space, "1.0.0");
        var stepsBefore = await StepsAsync((await ProjectAsync(first.ProjectId)).DeploymentProcessId);

        var second = await PublishAsync(space, "1.0.1");

        second.ProjectId.ShouldBe(first.ProjectId);
        second.ProjectCreated.ShouldBeFalse();
        second.ProcessUpdated.ShouldBeFalse();
        second.ReleaseId.ShouldNotBe(first.ReleaseId);
        second.DeploymentId.ShouldNotBe(first.DeploymentId);

        (await StepsAsync((await ProjectAsync(first.ProjectId)).DeploymentProcessId)).Select(s => s.Id).ShouldBe(stepsBefore.Select(s => s.Id), "the same step rows: nothing structural was rewritten");
        (await QueryAsync(r => r.QueryNoTracking<Project>().CountAsync())).ShouldBe(1);
        (await QueryAsync(r => r.QueryNoTracking<ReleaseEntity>().Select(x => x.Version).OrderBy(v => v).ToListAsync())).ShouldBe(["1.0.0", "1.0.1"]);
    }

    [Fact]
    public async Task PublishingTheSameVersionTwice_ReusesItsRelease_AndDeploysAgain()
    {
        var space = await SetUpAsync();

        await PushAsync(space.SpaceId, StaticSite(), "1.0.0");

        var first = await PublishAsync(space, "1.0.0");
        var again = await PublishAsync(space, "1.0.0");

        again.ReleaseId.ShouldBe(first.ReleaseId);
        again.DeploymentId.ShouldNotBe(first.DeploymentId);
        (await QueryAsync(r => r.QueryNoTracking<ReleaseEntity>().CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task ARecipeChange_UpdatesTheProcess_ForTheNextRelease()
    {
        var space = await SetUpAsync();

        await PushAsync(space.SpaceId, StaticSite(), "1.0.0");
        await PushAsync(space.SpaceId, DockerfileApp(), "1.1.0");

        var first = await PublishAsync(space, "1.0.0");
        var stepsBefore = await StepsAsync((await ProjectAsync(first.ProjectId)).DeploymentProcessId);

        var changed = await PublishAsync(space, "1.1.0");
        var project = await ProjectAsync(first.ProjectId);
        var stepsAfter = await StepsAsync(project.DeploymentProcessId);

        changed.ProjectId.ShouldBe(first.ProjectId);
        changed.ProcessUpdated.ShouldBeTrue();
        changed.RecipeKind.ShouldBe("dockerfile");
        stepsAfter.Select(s => s.Name).ShouldBe(["Deploy package", "Build and run"]);
        stepsAfter.Select(s => s.Id).ShouldNotBe(stepsBefore.Select(s => s.Id));
        PublishedProjectMarker.TryParse(project.Json).Recipe.ShouldBe("dockerfile");

        (await QueryAsync(r => r.QueryNoTracking<DeploymentStep>(s => s.ProcessId == project.DeploymentProcessId).CountAsync())).ShouldBe(2, "the old steps are gone, not left beside the new ones");
        (await QueryAsync(r => r.QueryNoTracking<DeploymentAction>().CountAsync())).ShouldBe(2, "no action of the old steps is left behind");
    }

    [Fact]
    public async Task ARecipeChange_ForAVersionThatAlreadyHasARelease_IsRefused_NotSilentlyServedTheOldProcess()
    {
        var space = await SetUpAsync();

        await PushAsync(space.SpaceId, Zip(("index.html", "x"), ("Dockerfile", "FROM busybox\nEXPOSE 8080\n")), "1.0.0");
        await PublishAsync(space, "1.0.0", recipe: "static-site");

        await Should.ThrowAsync<PublishReleaseVersionExistsException>(() => PublishAsync(space, "1.0.0", recipe: "dockerfile"));
    }

    [Fact]
    public async Task APackageNobodyCanRun_IsRefused_BeforeAnyProjectIsCreated()
    {
        var space = await SetUpAsync();

        await PushAsync(space.SpaceId, Zip(("notes.txt", "hello")), "1.0.0");

        await Should.ThrowAsync<PublishNoRecipeFitsException>(() => PublishAsync(space, "1.0.0"));

        (await QueryAsync(r => r.QueryNoTracking<Project>().CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task AProjectWithTheSameNameThatPublishingDidNotCreate_IsAConflict()
    {
        var space = await SetUpAsync();

        await PushAsync(space.SpaceId, StaticSite(), "1.0.0");
        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            await repository.InsertAsync(new Project { Name = "Taken", Slug = "taken", SpaceId = space.SpaceId, Json = string.Empty, IncludedLibraryVariableSetIds = "[]" });
            await unitOfWork.SaveChangesAsync();
        });

        await Should.ThrowAsync<PublishProjectNameConflictException>(() => PublishAsync(space, "1.0.0", name: "Taken"));
    }

    [Fact]
    public async Task PackagesOfOtherSpacesAndProjectsWithoutAMarker_AreNeverMistakenForTheAppsProject()
    {
        var space = await SetUpAsync();

        await PushAsync(space.SpaceId, StaticSite(), "1.0.0");
        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            await repository.InsertAsync(new Project { Name = PackageId, Slug = "other", SpaceId = space.SpaceId + 1000, Json = new PublishedProjectMarker(PackageId, "static-site", "x").ToJson(), IncludedLibraryVariableSetIds = "[]" });
            await unitOfWork.SaveChangesAsync();
        });

        var result = await PublishAsync(space, "1.0.0");

        result.ProjectCreated.ShouldBeTrue("a marked project of another space is not this space's project");
    }

    [Fact]
    public async Task ARefusedPublish_KeepsNothing_SoRetryingMeetsTheSameRefusal()
    {
        var space = await SetUpAsync();

        await PushAsync(space.SpaceId, Zip(("index.html", "x"), ("Dockerfile", "FROM busybox\nEXPOSE 8080\n")), "1.0.0");
        await PublishAsync(space, "1.0.0", recipe: "static-site");

        var project = (await QueryAsync(r => r.QueryNoTracking<Project>().SingleAsync()));
        var stepsBefore = (await StepsAsync(project.DeploymentProcessId)).Select(s => s.Id).ToList();
        var jsonBefore = project.Json;
        var releasesBefore = await CountAsync<ReleaseEntity>();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Should.ThrowAsync<PublishReleaseVersionExistsException>(() => PublishAsync(space, "1.0.0", recipe: "dockerfile"), $"attempt {attempt + 1}: a retry must be refused the same way, not silently deploy the old release");

            var after = await QueryAsync(r => r.QueryNoTracking<Project>().SingleAsync());

            (await StepsAsync(after.DeploymentProcessId)).Select(s => s.Id).ShouldBe(stepsBefore, "the refused publish must not have rewritten the process");
            after.Json.ShouldBe(jsonBefore, "nor the marker");
            (await CountAsync<ReleaseEntity>()).ShouldBe(releasesBefore);
            (await CountAsync<Deployment>()).ShouldBe(1);
        }
    }

    [Fact]
    public async Task FourPublishesAtOnce_OfANewPackage_CreateExactlyOneProject_AndNoneFails()
    {
        var space = await SetUpAsync();

        await PushAsync(space.SpaceId, StaticSite(), "1.0.0");

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PublishAsync(space, "1.0.0")));

        results.Select(r => r.ProjectId).Distinct().Count().ShouldBe(1);
        results.Count(r => r.ProjectCreated).ShouldBe(1);
        results.Select(r => r.ReleaseId).Distinct().Count().ShouldBe(1);
        (await CountAsync<Project>()).ShouldBe(1);
        (await CountAsync<ReleaseEntity>()).ShouldBe(1);
        (await CountAsync<Deployment>()).ShouldBe(4);
    }

    [Fact]
    public async Task AnEnvironmentThatIsOnlyInALaterPhase_IsRefusedAsAClearValidationFailure_AndNothingIsCreated()
    {
        var space = await SetUpAsync();
        var later = await AddEnvironmentAsync(space, "Live", Placement.SecondPhase);

        await PushAsync(space.SpaceId, StaticSite(), "1.0.0");

        var ex = await Should.ThrowAsync<PublishDefaultsMissingException>(() => PublishAsync(later, "1.0.0"));

        ex.Message.ShouldContain("lifecycle");
        (await CountAsync<Project>()).ShouldBe(0);
        (await CountAsync<ReleaseEntity>()).ShouldBe(0);
        (await CountAsync<Deployment>()).ShouldBe(0);
    }

    [Fact]
    public async Task AnExplicitLifecycleThatCannotDeployToTheEnvironment_IsRefusedByThePreview_AndNothingIsCreated()
    {
        var space = await SetUpAsync();
        var later = await AddEnvironmentAsync(space, "Live", Placement.SecondPhase);
        var lifecycleId = await QueryAsync(r => r.QueryNoTracking<Lifecycle>().Select(l => l.Id).SingleAsync());

        await PushAsync(space.SpaceId, StaticSite(), "1.0.0");

        var ex = await Should.ThrowAsync<Squid.Core.Services.DeploymentExecution.Exceptions.DeploymentValidationException>(() => PublishAsync(later, "1.0.0", lifecycleId: lifecycleId));

        ex.Message.ShouldContain("lifecycle");
        (await CountAsync<Project>()).ShouldBe(0);
        (await CountAsync<ReleaseEntity>()).ShouldBe(0);
        (await CountAsync<Squid.Core.Persistence.Entities.Deployments.VariableSet>()).ShouldBe(0);
        (await CountAsync<Deployment>()).ShouldBe(0);
    }

    [Fact]
    public async Task ARoleNoMachineHas_IsRefusedAsAClearValidationFailure_AndNothingIsCreated()
    {
        var space = await SetUpAsync();

        await PushAsync(space.SpaceId, StaticSite(), "1.0.0");

        var ex = await Should.ThrowAsync<Squid.Core.Services.DeploymentExecution.Exceptions.DeploymentValidationException>(() => PublishAsync(space, "1.0.0", role: "database"));

        ex.Message.ShouldContain("database");
        (await CountAsync<Project>()).ShouldBe(0);
        (await CountAsync<ReleaseEntity>()).ShouldBe(0);
        (await CountAsync<Deployment>()).ShouldBe(0);
    }

    [Fact]
    public async Task TheDefaultLifecycle_IsOneWhoseFirstPhaseListsTheEnvironment()
    {
        var space = await SetUpAsync();
        var later = await AddEnvironmentAsync(space, "Live", Placement.SecondPhase);

        // A second lifecycle that lists the later environment first, with a higher id than the first one.
        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var lifecycle = new Lifecycle { Name = "Live only", Slug = "live-only", SpaceId = space.SpaceId };

            await repository.InsertAsync(lifecycle);
            await unitOfWork.SaveChangesAsync();

            var phase = new LifecyclePhase { LifecycleId = lifecycle.Id, Name = "Live", SortOrder = 0 };

            await repository.InsertAsync(phase);
            await unitOfWork.SaveChangesAsync();
            await repository.InsertAsync(new LifecyclePhaseEnvironment { PhaseId = phase.Id, EnvironmentId = later.EnvironmentId, TargetType = Squid.Message.Enums.Deployments.LifecyclePhaseEnvironmentTargetType.Optional });
            await unitOfWork.SaveChangesAsync();
        });

        await PushAsync(space.SpaceId, StaticSite(), "1.0.0");

        var result = await PublishAsync(later, "1.0.0");

        (await ProjectAsync(result.ProjectId)).LifecycleId.ShouldBe(await QueryAsync(r => r.QueryNoTracking<Lifecycle>(l => l.Slug == "live-only").Select(l => l.Id).SingleAsync()));
    }

    [Fact]
    public async Task TwoEnvironments_GetTheirOwnPort_FromOneSharedProcess_AndTheSameVersionReusesItsRelease()
    {
        var space = await SetUpAsync();
        var staging = await AddEnvironmentAsync(space, "Staging");

        await PushAsync(space.SpaceId, StaticSite(), "1.0.0");

        var first = await PublishAsync(space, "1.0.0", port: 8080);
        var stepsBefore = (await StepsAsync((await ProjectAsync(first.ProjectId)).DeploymentProcessId)).Select(s => s.Id).ToList();

        var second = await PublishAsync(staging, "1.0.0", port: 8081);

        second.ProjectId.ShouldBe(first.ProjectId);
        second.ProcessUpdated.ShouldBeFalse("a port for another environment does not change the shared process");
        second.ReleaseId.ShouldBe(first.ReleaseId, "the same version is the same release; its variable snapshot is refreshed");
        (await StepsAsync((await ProjectAsync(first.ProjectId)).DeploymentProcessId)).Select(s => s.Id).ShouldBe(stepsBefore);

        (await InputVariablesAsync(first.ProjectId)).Select(v => (v.Name, v.Value, v.Scope)).ToList().ShouldBe(
            new[] { (space.EnvironmentId.ToString(), "8080"), (staging.EnvironmentId.ToString(), "8081") }.OrderBy(x => x.Item1, StringComparer.Ordinal).Select(x => ("Squid.Publish.Input.hostPort", x.Item2, x.Item1)).ToList());

        // Changing one environment's port changes that environment's variable only.
        await PublishAsync(staging, "1.0.0", port: 9091);

        (await InputVariablesAsync(first.ProjectId)).Select(v => v.Value).OrderBy(v => v).ShouldBe(["8080", "9091"]);
        (await CountAsync<Squid.Core.Persistence.Entities.Deployments.Variable>()).ShouldBe(2);
    }

    [Fact]
    public async Task PublishingAnEnvironmentsPort_MovesTheVariableSetsVersion_SoAStaleVariablesPageSaveIsRefused()
    {
        var space = await SetUpAsync();
        var staging = await AddEnvironmentAsync(space, "Staging");

        await PushAsync(space.SpaceId, StaticSite(), "1.0.0");

        var first = await PublishAsync(space, "1.0.0", port: 8080);
        var variableSetId = (await ProjectAsync(first.ProjectId)).VariableSetId;

        // The user opens the Variables page now ...
        var opened = await Run<IVariableService, VariableSetDto>(s => s.GetVariableSetByIdAsync(variableSetId, CancellationToken.None));

        // ... a publish adds Staging's port meanwhile ...
        await PublishAsync(staging, "1.0.0", port: 8081);

        // ... and the user saves what they read: the save must be refused, not delete Staging's port.
        var stale = new UpdateVariableSetCommand { Id = variableSetId, Name = opened.Name, Description = opened.Description, OwnerId = opened.OwnerId, OwnerType = opened.OwnerType, SpaceId = opened.SpaceId, Version = opened.Version, Variables = [] };

        await Should.ThrowAsync<VariableSetVersionConflictException>(() => Run<IVariableService, VariableSetDto>(s => s.UpdateVariableSetAsync(stale, CancellationToken.None)));

        (await InputVariablesAsync(first.ProjectId)).Select(v => v.Value).OrderBy(v => v).ShouldBe(["8080", "8081"]);
    }

    [Fact]
    public async Task ARecipeChange_KeepsWhatElseIsInTheProjectsJson()
    {
        var space = await SetUpAsync();

        await PushAsync(space.SpaceId, StaticSite(), "1.0.0");
        await PushAsync(space.SpaceId, DockerfileApp(), "1.1.0");

        var first = await PublishAsync(space, "1.0.0");

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var project = await repository.Query<Project>(p => p.Id == first.ProjectId).SingleAsync();

            project.Json = PublishedProjectMarker.TryParse(project.Json) != null ? new PublishedProjectMarker("Acme.Web", "static-site", "x").ToJson("{\"description\":\"My shop\"}") : project.Json;

            await unitOfWork.SaveChangesAsync();
        });

        await PublishAsync(space, "1.1.0");

        var json = (await ProjectAsync(first.ProjectId)).Json;

        json.ShouldContain("\"description\":\"My shop\"");
        PublishedProjectMarker.TryParse(json).Recipe.ShouldBe("dockerfile");
    }

    [Fact]
    public async Task TheOldestMarkedProject_IsTheOneFound_WhateverOrderTheDatabaseReturns()
    {
        var space = await SetUpAsync();

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            foreach (var name in new[] { "Second", "First" })
                await repository.InsertAsync(new Project { Name = name, Slug = name.ToLowerInvariant(), SpaceId = space.SpaceId, Json = new PublishedProjectMarker(PackageId, "static-site", "x").ToJson(), IncludedLibraryVariableSetIds = "[]" });

            await unitOfWork.SaveChangesAsync();
        });

        var found = await Run<IPublishedProjectDataProvider, Project>(p => p.FindByPackageAsync(space.SpaceId, PackageId, CancellationToken.None));

        found.Name.ShouldBe("Second", "inserted first, so the lowest id");
    }
}
