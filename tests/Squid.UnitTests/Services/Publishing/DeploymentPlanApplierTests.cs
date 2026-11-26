using System.Linq;
using Squid.Core.Services.Deployments.Channels;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Publishing;
using Squid.Core.Services.Publishing.Exceptions;
using Squid.Message.Commands.Deployments.Process.Step;
using Squid.Message.Commands.Deployments.Project;
using Squid.Message.Events.Deployments.Project;
using Squid.Message.Models.Deployments.Project;
using Squid.Message.Models.Publishing;
using ChannelEntity = Squid.Core.Persistence.Entities.Deployments.Channel;
using ProjectEntity = Squid.Core.Persistence.Entities.Deployments.Project;

namespace Squid.UnitTests.Services.Publishing;

public class DeploymentPlanApplierTests
{
    private const int SpaceId = 3;
    private const int ProcessId = 50;
    private const int ProjectId = 7;
    private const int ChannelId = 70;
    private const int EnvironmentId = 5;

    private readonly Mock<IProjectService> _projects = new();
    private readonly Mock<IProjectDataProvider> _projectData = new();
    private readonly Mock<IPublishedProjectDataProvider> _published = new();
    private readonly Mock<IDeploymentStepService> _steps = new();
    private readonly Mock<IDeploymentStepDataProvider> _stepData = new();
    private readonly Mock<IChannelDataProvider> _channels = new();
    private readonly List<CreateProjectCommand> _createdProjects = new();
    private readonly List<CreateDeploymentStepCommand> _createdSteps = new();

    public DeploymentPlanApplierTests()
    {
        _projects.Setup(p => p.CreateProjectAsync(It.IsAny<CreateProjectCommand>(), It.IsAny<CancellationToken>()))
            .Callback<CreateProjectCommand, CancellationToken>((c, _) => _createdProjects.Add(c))
            .ReturnsAsync(new ProjectCreatedEvent { Data = new ProjectDto { Id = ProjectId } });

        _projectData.Setup(d => d.GetProjectByIdAsync(ProjectId, It.IsAny<CancellationToken>())).ReturnsAsync(() => NewProject(_createdProjects.LastOrDefault()?.Project.Json));
        _steps.Setup(s => s.CreateDeploymentStepAsync(It.IsAny<CreateDeploymentStepCommand>(), It.IsAny<CancellationToken>())).Callback<CreateDeploymentStepCommand, CancellationToken>((c, _) => _createdSteps.Add(c)).ReturnsAsync(new Squid.Message.Events.Deployments.Step.DeploymentStepCreatedEvent());
        _channels.Setup(c => c.GetDefaultChannelByProjectIdAsync(ProjectId, It.IsAny<CancellationToken>())).ReturnsAsync(new ChannelEntity { Id = ChannelId });
        _published.Setup(p => p.FirstProjectGroupIdAsync(SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(11);
        _published.Setup(p => p.FirstLifecycleIdAsync(SpaceId, EnvironmentId, It.IsAny<CancellationToken>())).ReturnsAsync(22);
    }

    private readonly List<(int Set, string Name, int Env, string Value)> _upserts = new();
    private bool _upsertChanges = true;

    private void CaptureUpserts()
    {
        _published.Setup(p => p.UpsertEnvironmentVariableAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<int, string, int, string, CancellationToken>((set, name, env, value, _) => _upserts.Add((set, name, env, value)))
            .ReturnsAsync(() => _upsertChanges);
    }

    private DeploymentPlanApplier Applier() => new(_projects.Object, _projectData.Object, _published.Object, _steps.Object, _stepData.Object, _channels.Object);

    private static ProjectEntity NewProject(string json) => new() { Id = ProjectId, SpaceId = SpaceId, DeploymentProcessId = ProcessId, Json = json };

    private static DeploymentPlan Plan(string script = "echo hi")
    {
        return new DeploymentPlan
        {
            Steps =
            [
                new PlanStep { Name = "Deploy package", TargetRoles = ["web", "edge"], Actions = [new PlanAction { Name = "Deploy package", ActionType = "Squid.TentaclePackage", Package = new PlanPackageReference { PackageId = "Acme.Web" }, Properties = { ["Squid.Action.Package.InstallationDirectoryMode"] = "Versioned" } }] },
                new PlanStep { Name = "Run", TargetRoles = ["web", "edge"], Actions = [new PlanAction { Name = "Run", ActionType = "Squid.Script", Properties = { ["Squid.Action.Script.ScriptBody"] = script } }] }
            ]
        };
    }

    private static PlanApplication Application(DeploymentPlan plan, string kind = "static-site", string name = null) => new() { SpaceId = SpaceId, FeedId = 9, EnvironmentId = EnvironmentId, PackageId = "Acme.Web", RecipeKind = kind, ProjectName = name, Plan = plan, InputValues = new() { ["hostPort"] = "8080" } };

    private void ProjectExistsWithMarker(string hash, string kind = "static-site")
    {
        var project = NewProject(new PublishedProjectMarker("Acme.Web", kind, hash).ToJson());

        _published.Setup(p => p.FindByPackageAsync(SpaceId, "Acme.Web", It.IsAny<CancellationToken>())).ReturnsAsync(project);
        _projectData.Setup(d => d.GetProjectByIdAsync(ProjectId, It.IsAny<CancellationToken>())).ReturnsAsync(project);
    }

    [Fact]
    public async Task NewPackage_CreatesTheProject_WithTheMarker_AndBuildsTheProcess()
    {
        var plan = Plan();

        var applied = await Applier().ApplyAsync(Application(plan, name: "  My App "), CancellationToken.None);

        var created = _createdProjects.Single().Project;

        created.Name.ShouldBe("My App");
        created.SpaceId.ShouldBe(SpaceId);
        created.ProjectGroupId.ShouldBe(11);
        created.LifecycleId.ShouldBe(22);
        PublishedProjectMarker.TryParse(created.Json).PackageId.ShouldBe("Acme.Web");

        _createdSteps.Select(c => c.Step.Name).ShouldBe(["Deploy package", "Run"]);
        _createdSteps.ShouldAllBe(c => c.ProcessId == ProcessId && c.SpaceId == SpaceId);
        applied.ShouldSatisfyAllConditions(a => a.ProjectId.ShouldBe(ProjectId), a => a.ChannelId.ShouldBe(ChannelId), a => a.ProjectCreated.ShouldBeTrue(), a => a.ProcessUpdated.ShouldBeFalse(), a => a.PackageActionNames.ShouldBe(["Deploy package"]));
    }

    [Fact]
    public async Task NewPackage_RecordsTheFingerprint_AfterTheStepsExist()
    {
        var plan = Plan();
        ProjectEntity updated = null;

        _projectData.Setup(d => d.UpdateProjectAsync(It.IsAny<ProjectEntity>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Callback<ProjectEntity, bool, CancellationToken>((p, _, _) => updated = p).Returns(Task.CompletedTask);

        await Applier().ApplyAsync(Application(plan), CancellationToken.None);

        PublishedProjectMarker.TryParse(updated.Json).ShouldBe(new PublishedProjectMarker("Acme.Web", "static-site", DeploymentPlanFingerprint.Of(plan)));
    }

    [Fact]
    public async Task TheRecipesInputs_AreStoredAsVariablesScopedToTheEnvironmentBeingPublished()
    {
        CaptureUpserts();

        var applied = await Applier().ApplyAsync(Application(Plan()), CancellationToken.None);

        _upserts.Single().ShouldBe((0, "Squid.Publish.Input.hostPort", EnvironmentId, "8080"));
        applied.InputsChanged.ShouldBeTrue();
    }

    [Fact]
    public async Task AnUnchangedInput_ReportsNoChange_AndTheProcessIsStillUntouched()
    {
        CaptureUpserts();
        _upsertChanges = false;

        var plan = Plan();

        ProjectExistsWithMarker(DeploymentPlanFingerprint.Of(plan));

        var applied = await Applier().ApplyAsync(Application(plan), CancellationToken.None);

        applied.InputsChanged.ShouldBeFalse();
        applied.ProcessUpdated.ShouldBeFalse();
        _steps.Verify(s => s.CreateDeploymentStepAsync(It.IsAny<CreateDeploymentStepCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task APortChange_ForAnEnvironment_NeverRewritesTheSharedProcess()
    {
        CaptureUpserts();

        var plan = Plan();

        ProjectExistsWithMarker(DeploymentPlanFingerprint.Of(plan));

        var application = Application(plan);

        application.InputValues["hostPort"] = "9999";

        var applied = await Applier().ApplyAsync(application, CancellationToken.None);

        applied.InputsChanged.ShouldBeTrue();
        applied.ProcessUpdated.ShouldBeFalse("the port lives in a variable for the environment, not in the process every environment shares");
        _stepData.Verify(d => d.DeleteDeploymentStepsByProcessIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordingTheMarker_KeepsWhatElseIsInTheProjectsJson()
    {
        var plan = Plan();
        var project = NewProject("{\"description\":\"My shop\",\"squidPublish\":{\"packageId\":\"Acme.Web\",\"recipe\":\"static-site\",\"planHash\":\"old\"}}");

        _published.Setup(p => p.FindByPackageAsync(SpaceId, "Acme.Web", It.IsAny<CancellationToken>())).ReturnsAsync(project);
        _projectData.Setup(d => d.GetProjectByIdAsync(ProjectId, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        await Applier().ApplyAsync(Application(plan), CancellationToken.None);

        System.Text.Json.Nodes.JsonNode.Parse(project.Json)["description"].ToString().ShouldBe("My shop");
        PublishedProjectMarker.TryParse(project.Json).PlanHash.ShouldBe(DeploymentPlanFingerprint.Of(plan));
    }

    [Fact]
    public async Task NewPackage_WithoutAName_UsesThePackageId()
    {
        await Applier().ApplyAsync(Application(Plan()), CancellationToken.None);

        _createdProjects.Single().Project.Name.ShouldBe("Acme.Web");
    }

    [Fact]
    public async Task StepsCarryTheRolesAndTheBuiltInFeedPackage()
    {
        await Applier().ApplyAsync(Application(Plan()), CancellationToken.None);

        var deploy = _createdSteps[0].Step;
        var action = deploy.Actions.Single();

        deploy.Properties.Single().ShouldSatisfyAllConditions(p => p.PropertyName.ShouldBe("Squid.Action.TargetRoles"), p => p.PropertyValue.ShouldBe("web,edge"));
        action.Properties.ShouldContain(p => p.PropertyName == "Squid.Action.Package.FeedId" && p.PropertyValue == "9");
        action.Properties.ShouldContain(p => p.PropertyName == "Squid.Action.Package.PackageId" && p.PropertyValue == "Acme.Web");
        _createdSteps[1].Step.Actions.Single().Properties.ShouldNotContain(p => p.PropertyName == "Squid.Action.Package.FeedId");
    }

    [Fact]
    public async Task SamePlanAgain_ChangesNothingStructural()
    {
        var plan = Plan();

        ProjectExistsWithMarker(DeploymentPlanFingerprint.Of(plan));

        var applied = await Applier().ApplyAsync(Application(plan), CancellationToken.None);

        _projects.Verify(p => p.CreateProjectAsync(It.IsAny<CreateProjectCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        _stepData.Verify(d => d.DeleteDeploymentStepsByProcessIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _steps.Verify(s => s.CreateDeploymentStepAsync(It.IsAny<CreateDeploymentStepCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        _projectData.Verify(d => d.UpdateProjectAsync(It.IsAny<ProjectEntity>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        applied.ProjectCreated.ShouldBeFalse();
        applied.ProcessUpdated.ShouldBeFalse();
        applied.ProjectId.ShouldBe(ProjectId);
    }

    [Fact]
    public async Task ChangedPlan_RebuildsTheProcess_AndRecordsTheNewFingerprint()
    {
        ProjectExistsWithMarker(DeploymentPlanFingerprint.Of(Plan("echo old")));
        ProjectEntity updated = null;

        _projectData.Setup(d => d.UpdateProjectAsync(It.IsAny<ProjectEntity>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Callback<ProjectEntity, bool, CancellationToken>((p, _, _) => updated = p).Returns(Task.CompletedTask);

        var newPlan = Plan("echo new");
        var applied = await Applier().ApplyAsync(Application(newPlan), CancellationToken.None);

        _stepData.Verify(d => d.DeleteDeploymentStepsByProcessIdAsync(ProcessId, It.IsAny<CancellationToken>()), Times.Once);
        _createdSteps.Count.ShouldBe(2);
        PublishedProjectMarker.TryParse(updated.Json).PlanHash.ShouldBe(DeploymentPlanFingerprint.Of(newPlan));
        applied.ProcessUpdated.ShouldBeTrue();
        applied.ProjectCreated.ShouldBeFalse();
    }

    [Fact]
    public async Task ChangedRecipe_RebuildsTheProcess_EvenWithTheSameFingerprint()
    {
        var plan = Plan();

        ProjectExistsWithMarker(DeploymentPlanFingerprint.Of(plan), kind: "dockerfile");

        var applied = await Applier().ApplyAsync(Application(plan, kind: "static-site"), CancellationToken.None);

        applied.ProcessUpdated.ShouldBeTrue();
    }

    [Fact]
    public async Task HalfBuiltProject_WithAnEmptyFingerprint_IsRebuilt()
    {
        ProjectExistsWithMarker(string.Empty);

        (await Applier().ApplyAsync(Application(Plan()), CancellationToken.None)).ProcessUpdated.ShouldBeTrue();
    }

    [Fact]
    public async Task ExistingProject_IgnoresTheRequestedName()
    {
        var plan = Plan();

        ProjectExistsWithMarker(DeploymentPlanFingerprint.Of(plan));

        await Applier().ApplyAsync(Application(plan, name: "Another Name"), CancellationToken.None);

        _published.Verify(p => p.ProjectNameExistsAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NameTakenByAnotherProject_IsAConflict_AndNothingIsCreated()
    {
        _published.Setup(p => p.ProjectNameExistsAsync(SpaceId, "Acme.Web", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var ex = await Should.ThrowAsync<PublishProjectNameConflictException>(() => Applier().ApplyAsync(Application(Plan()), CancellationToken.None));

        ex.Name.ShouldBe("Acme.Web");
        _projects.Verify(p => p.CreateProjectAsync(It.IsAny<CreateProjectCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true, false, "project group")]
    [InlineData(false, true, "lifecycle that lists environment 5")]
    public async Task NoDefaultToPutTheProjectIn_AndNoneNamed_IsReported(bool noGroup, bool noLifecycle, string expected)
    {
        if (noGroup) _published.Setup(p => p.FirstProjectGroupIdAsync(SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync((int?)null);
        if (noLifecycle) _published.Setup(p => p.FirstLifecycleIdAsync(SpaceId, EnvironmentId, It.IsAny<CancellationToken>())).ReturnsAsync((int?)null);

        var ex = await Should.ThrowAsync<PublishDefaultsMissingException>(() => Applier().ApplyAsync(Application(Plan()), CancellationToken.None));

        ex.What.ShouldBe(expected);
    }

    [Fact]
    public async Task NamedGroupAndLifecycle_AreUsed_WithoutLookingForDefaults()
    {
        var application = Application(Plan());

        application.ProjectGroupId = 101;
        application.LifecycleId = 202;

        await Applier().ApplyAsync(application, CancellationToken.None);

        _createdProjects.Single().Project.ProjectGroupId.ShouldBe(101);
        _createdProjects.Single().Project.LifecycleId.ShouldBe(202);
        _published.Verify(p => p.FirstProjectGroupIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
