using System.Linq;
using Squid.Core.Services.Deployments.Channels;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.Core.Services.Deployments.Release;
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
    private readonly Mock<IDeploymentStepPropertyDataProvider> _stepProperties = new();
    private readonly Mock<IReleaseDataProvider> _releaseData = new();
    private readonly Mock<IChannelDataProvider> _channels = new();
    private readonly List<CreateProjectCommand> _createdProjects = new();
    private readonly List<CreateDeploymentStepCommand> _createdSteps = new();
    private readonly List<(CreateDeploymentStepCommand Command, int Order)> _insertedSteps = new();
    private List<DeploymentStep> _existingSteps = new();
    private List<DeploymentStepProperty> _existingProperties = new();

    public DeploymentPlanApplierTests()
    {
        _projects.Setup(p => p.CreateProjectAsync(It.IsAny<CreateProjectCommand>(), It.IsAny<CancellationToken>()))
            .Callback<CreateProjectCommand, CancellationToken>((c, _) => _createdProjects.Add(c))
            .ReturnsAsync(new ProjectCreatedEvent { Data = new ProjectDto { Id = ProjectId } });

        _projectData.Setup(d => d.GetProjectByIdAsync(ProjectId, It.IsAny<CancellationToken>())).ReturnsAsync(() => NewProject(_createdProjects.LastOrDefault()?.Project.Json));
        _steps.Setup(s => s.CreateDeploymentStepAsync(It.IsAny<CreateDeploymentStepCommand>(), It.IsAny<CancellationToken>())).Callback<CreateDeploymentStepCommand, CancellationToken>((c, _) => _createdSteps.Add(c)).ReturnsAsync(new Squid.Message.Events.Deployments.Step.DeploymentStepCreatedEvent());
        _steps.Setup(s => s.InsertDeploymentStepAsync(It.IsAny<CreateDeploymentStepCommand>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Callback<CreateDeploymentStepCommand, int, CancellationToken>((c, order, _) => _insertedSteps.Add((c, order))).ReturnsAsync(new Squid.Message.Events.Deployments.Step.DeploymentStepCreatedEvent());
        _stepData.Setup(d => d.GetDeploymentStepsByProcessIdAsync(ProcessId, It.IsAny<CancellationToken>())).ReturnsAsync(() => _existingSteps);
        _stepProperties.Setup(d => d.GetDeploymentStepPropertiesByStepIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _existingProperties);
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

    private DeploymentPlanApplier Applier() => new(_projects.Object, _projectData.Object, _published.Object, _steps.Object, _stepData.Object, _stepProperties.Object, _releaseData.Object, _channels.Object);

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
        _stepData.Verify(d => d.DeleteDeploymentStepsAsync(It.IsAny<List<DeploymentStep>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
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

        deploy.Properties.Single(p => p.PropertyName == "Squid.Action.TargetRoles").PropertyValue.ShouldBe("web,edge");
        deploy.Properties.ShouldContain(p => p.PropertyName == PublishedProjectMarker.OwnedStepProperty && p.PropertyValue == "true");
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
        _stepData.Verify(d => d.DeleteDeploymentStepsAsync(It.IsAny<List<DeploymentStep>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
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
        ProcessHas([Step(1, 1), Step(2, 2)], Owned(1), Owned(2), Roles(1, "web,edge"), Roles(2, "web,edge"));
        ProjectEntity updated = null;

        _projectData.Setup(d => d.UpdateProjectAsync(It.IsAny<ProjectEntity>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Callback<ProjectEntity, bool, CancellationToken>((p, _, _) => updated = p).Returns(Task.CompletedTask);

        var newPlan = Plan("echo new");
        var applied = await Applier().ApplyAsync(Application(newPlan), CancellationToken.None);

        _stepData.Verify(d => d.DeleteDeploymentStepsAsync(It.IsAny<List<DeploymentStep>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
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

    // ---- Which steps publishing owns, and where the rebuilt ones go

    private static DeploymentStep Step(int id, int order) => new() { Id = id, ProcessId = ProcessId, StepOrder = order, Name = $"step {id}" };

    private static DeploymentStepProperty Owned(int stepId) => new() { StepId = stepId, PropertyName = PublishedProjectMarker.OwnedStepProperty, PropertyValue = "true" };

    private static DeploymentStepProperty Roles(int stepId, string roles) => new() { StepId = stepId, PropertyName = "Squid.Action.TargetRoles", PropertyValue = roles };

    private List<DeploymentStep> _deleted;

    private void CaptureDeletedSteps()
    {
        _stepData.Setup(d => d.DeleteDeploymentStepsAsync(It.IsAny<List<DeploymentStep>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Callback<List<DeploymentStep>, bool, CancellationToken>((steps, _, _) => _deleted = steps).Returns(Task.CompletedTask);
    }

    private void ProcessHas(List<DeploymentStep> steps, params DeploymentStepProperty[] properties)
    {
        _existingSteps = steps;
        _existingProperties = properties.ToList();
    }

    [Fact]
    public async Task AChangedPlan_ReplacesOnlyTheStepsPublishingOwns_AndKeepsTheOnesAPersonAdded()
    {
        CaptureDeletedSteps();
        ProjectExistsWithMarker(DeploymentPlanFingerprint.Of(Plan("echo old")));
        ProcessHas([Step(1, 1), Step(2, 2), Step(3, 3)], Owned(1), Owned(2), Roles(1, "web,edge"), Roles(2, "web,edge"));

        await Applier().ApplyAsync(Application(Plan("echo new")), CancellationToken.None);

        _deleted.Select(s => s.Id).ShouldBe([1, 2], "step 3 has no publishing marker: a person added it");
    }

    [Fact]
    public async Task TheRebuiltSteps_TakeThePlaceOfTheFirstReplacedOne_WhenAPersonsStepsAreKept()
    {
        CaptureDeletedSteps();
        ProjectExistsWithMarker(DeploymentPlanFingerprint.Of(Plan("echo old")));
        ProcessHas([Step(9, 1), Step(1, 2), Step(2, 3), Step(3, 4)], Owned(1), Owned(2), Roles(1, "web,edge"), Roles(2, "web,edge"));

        await Applier().ApplyAsync(Application(Plan("echo new")), CancellationToken.None);

        _insertedSteps.Select(i => (i.Command.Step.Name, i.Order)).ShouldBe([("Deploy package", 2), ("Run", 3)]);
        _createdSteps.ShouldBeEmpty("with a kept step the rebuilt ones are inserted at a place, not appended");
    }

    [Fact]
    public async Task WithNothingKept_TheRebuiltStepsAreAppendedAsBefore()
    {
        CaptureDeletedSteps();
        ProjectExistsWithMarker(DeploymentPlanFingerprint.Of(Plan("echo old")));
        ProcessHas([Step(1, 1), Step(2, 2)], Owned(1), Owned(2), Roles(1, "web,edge"), Roles(2, "web,edge"));

        await Applier().ApplyAsync(Application(Plan("echo new")), CancellationToken.None);

        _createdSteps.Count.ShouldBe(2);
        _insertedSteps.ShouldBeEmpty();
    }

    [Fact]
    public async Task AProcessWithOnlyUnmarkedSteps_KeepsAllOfThem_AndARePublishAddsTheOwnedOnes()
    {
        CaptureDeletedSteps();
        ProjectExistsWithMarker(DeploymentPlanFingerprint.Of(Plan("echo old")));
        ProcessHas([Step(1, 1), Step(2, 2)], Roles(1, "database"), Roles(2, "database"));

        await Applier().ApplyAsync(Application(Plan("echo new")), CancellationToken.None);

        _stepData.Verify(d => d.DeleteDeploymentStepsAsync(It.IsAny<List<DeploymentStep>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _createdSteps.Select(c => c.Step.Name).ShouldBe(["Deploy package", "Run"]);
        _createdSteps.ShouldAllBe(c => c.Step.Properties.Any(p => p.PropertyName == PublishedProjectMarker.OwnedStepProperty && p.PropertyValue == "true"));
    }

    [Fact]
    public async Task APersonsStepsRoles_AreNotTakenForTheRolesOfThePublishedApp()
    {
        CaptureDeletedSteps();
        ProjectExistsWithMarker(DeploymentPlanFingerprint.Of(Plan("echo old")));
        ProcessHas([Step(1, 1), Step(2, 2), Step(3, 3)], Owned(1), Owned(2), Roles(1, "web,edge"), Roles(2, "web,edge"), Roles(3, "database"));

        await Applier().ApplyAsync(Application(Plan("echo new")), CancellationToken.None);

        _insertedSteps.ShouldAllBe(i => i.Command.Step.Properties.Single(p => p.PropertyName == "Squid.Action.TargetRoles").PropertyValue == "web,edge");
    }

    // ---- Roles are shared by every environment

    private DeploymentPlan PlanWithRoles(params string[] roles)
    {
        var plan = Plan();

        plan.Steps.ForEach(s => s.TargetRoles = roles.ToList());

        return plan;
    }

    [Fact]
    public async Task ARoleNoPublishedStepTargets_IsRefused_BeforeAnythingIsChanged()
    {
        CaptureDeletedSteps();
        ProjectExistsWithMarker(DeploymentPlanFingerprint.Of(PlanWithRoles("prod-web")));
        ProcessHas([Step(1, 1), Step(2, 2)], Owned(1), Owned(2), Roles(1, "web"), Roles(2, "web"));

        var ex = await Should.ThrowAsync<PublishTargetRolesConflictException>(() => Applier().ApplyAsync(Application(PlanWithRoles("prod-web")), CancellationToken.None));

        ex.ExistingRoles.ShouldBe(["web"]);
        ex.RequestedRoles.ShouldBe(["prod-web"]);
        ex.Message.ShouldContain("prod-web");
        _stepData.Verify(d => d.DeleteDeploymentStepsAsync(It.IsAny<List<DeploymentStep>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ARoleThatDiffersOnlyInCase_IsTheSameRole()
    {
        var plan = PlanWithRoles("WEB");

        ProjectExistsWithMarker("old");
        ProcessHas([Step(1, 1), Step(2, 2)], Owned(1), Owned(2), Roles(1, "web"), Roles(2, "web"));

        await Should.NotThrowAsync(() => Applier().ApplyAsync(Application(plan), CancellationToken.None));
    }

    [Fact]
    public async Task AskingForSomeOfTheExistingRoles_KeepsAllOfThem_AndIsNotAPlanChange()
    {
        var plan = Plan();

        ProjectExistsWithMarker(DeploymentPlanFingerprint.Of(plan));
        ProcessHas([Step(1, 1), Step(2, 2)], Owned(1), Owned(2), Roles(1, "web,edge"), Roles(2, "web,edge"));

        var requested = PlanWithRoles("web");
        var applied = await Applier().ApplyAsync(Application(requested), CancellationToken.None);

        applied.ProcessUpdated.ShouldBeFalse("the steps keep web,edge, so the fingerprint is the recorded one");
        _stepData.Verify(d => d.DeleteDeploymentStepsAsync(It.IsAny<List<DeploymentStep>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AFirstPublish_TakesTheRolesAsked()
    {
        await Applier().ApplyAsync(Application(PlanWithRoles("prod-web")), CancellationToken.None);

        _createdSteps.ShouldAllBe(c => c.Step.Properties.Single(p => p.PropertyName == "Squid.Action.TargetRoles").PropertyValue == "prod-web");
    }

    // ---- A version that has a release is promoted, not rebuilt for

    private void ReleaseExists(string version = "1.0.0") => _releaseData.Setup(r => r.GetReleaseByVersionAsync(ProjectId, ChannelId, version, It.IsAny<CancellationToken>())).ReturnsAsync(new Squid.Core.Persistence.Entities.Deployments.Release { Id = 55 });

    [Fact]
    public async Task APlanThatChanged_LeavesTheProcessAlone_WhenTheVersionAlreadyHasARelease()
    {
        CaptureDeletedSteps();
        ProjectExistsWithMarker(DeploymentPlanFingerprint.Of(Plan("echo old")));
        ProcessHas([Step(1, 1), Step(2, 2)], Owned(1), Owned(2), Roles(1, "web,edge"), Roles(2, "web,edge"));
        ReleaseExists();

        var application = Application(Plan("echo new"));

        application.PackageVersion = "1.0.0";

        var applied = await Applier().ApplyAsync(application, CancellationToken.None);

        applied.ProcessUpdated.ShouldBeFalse();
        _stepData.Verify(d => d.DeleteDeploymentStepsAsync(It.IsAny<List<DeploymentStep>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _projectData.Verify(d => d.UpdateProjectAsync(It.IsAny<ProjectEntity>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task APlanThatChanged_StillRebuildsTheProcess_ForAVersionWithoutARelease()
    {
        CaptureDeletedSteps();
        ProjectExistsWithMarker(DeploymentPlanFingerprint.Of(Plan("echo old")));
        ProcessHas([Step(1, 1), Step(2, 2)], Owned(1), Owned(2), Roles(1, "web,edge"), Roles(2, "web,edge"));
        ReleaseExists("0.9.0");

        var application = Application(Plan("echo new"));

        application.PackageVersion = "1.0.0";

        (await Applier().ApplyAsync(application, CancellationToken.None)).ProcessUpdated.ShouldBeTrue();
    }
}
