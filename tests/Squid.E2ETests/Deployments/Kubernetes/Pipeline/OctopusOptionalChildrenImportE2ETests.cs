using System.Net;
using Autofac;
using Mediator.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.IntegrationTests.Helpers;
using Squid.IntegrationTests.Services.OctopusImport;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Commands.Deployments.Process.Step;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Shouldly;
using Xunit;
using SquidEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity tier for the choices a re-import offers, Pipeline pattern: a real-socket stub Octopus serves the configuration, the
/// production REST client, planner, validator and confirmation import it through the mediator into the real database, and a release is
/// deployed through the real <see cref="IDeploymentTaskExecutor"/> (only the transport's execution strategy is captured, so the order the
/// steps run in is observed, nothing is re-implemented). In Squid a step and a channel are deleted; Octopus adds a step between two steps,
/// a channel with a release, and a lifecycle phase; a variable is deleted in Squid and Octopus adds two variables sharing a name, one scoped
/// to the deployed environment. The preview offers them all with their reasons and defaults; the deleted step, the added step and both
/// namesake variables are ticked; the deployed release runs every step in Octopus order, the inserted one included, with the scoped value;
/// the unticked channel and variable stay deleted; a last import is up to date, still offers what was declined and writes nothing. Its own
/// class (own database).
/// </summary>
[Trait("Category", "E2E")]
public class OctopusOptionalChildrenImportE2ETests : IClassFixture<OctopusOptionalChildrenImportE2ETests.ImportFixture>
{
    private const int SpaceId = 1;
    private const string ApiKey = "API-E2E-OPTIONAL-CHILDREN-1";

    private readonly ImportFixture _fixture;

    public OctopusOptionalChildrenImportE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Import_DeleteAndAddChildren_PreviewOffersBoth_SelectedOnesImported_DeployRunsInOctopusOrder()
    {
        await EnsureSpaceAsync();
        await using var octopus = StubOctopusServer.Start(ApiKey);
        Process(octopus, "Build", "Deploy", "Smoke");
        Channels(octopus, ("Channels-2", "Hotfix"));
        Variables(octopus, Greeting, Farewell);
        (await ImportAsync(octopus, plan => plan)).Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);

        await DeleteStepInSquidAsync("Smoke");
        await DeleteChannelInSquidAsync("Hotfix");
        await DeleteVariableInSquidAsync("Farewell");
        Process(octopus, "Build", "Migrate", "Deploy", "Smoke");
        Channels(octopus, ("Channels-2", "Hotfix"), ("Channels-3", "Canary"));
        Phases(octopus, "TEST", "STAGING");
        Variables(octopus, Greeting, Farewell, ApiUrlEverywhere, ApiUrlOnTest);

        var import = await ImportAsync(octopus, plan => Tick(Tick(Tick(Tick(plan, "Projects-1/step/Step-Smoke"), "Projects-1/step/Step-Migrate"), $"Projects-1/variable/{ApiUrlEverywhere.Id}"), $"Projects-1/variable/{ApiUrlOnTest.Id}"));

        var offered = import.PreviewPlan.Resources.Where(r => r.Choice is { FollowsSourceId: null }).Select(r => (r.SourceName, r.Choice.Reason, r.Choice.DefaultSelected)).ToList();
        offered.ShouldBe([("Smoke", OctopusImportChoiceReason.DeletedInSquid, false), ("Hotfix", OctopusImportChoiceReason.DeletedInSquid, false), ("Farewell", OctopusImportChoiceReason.DeletedInSquid, false), ("Migrate", OctopusImportChoiceReason.AddedInOctopus, false), ("Canary", OctopusImportChoiceReason.AddedInOctopus, true), ("STAGING", OctopusImportChoiceReason.AddedInOctopus, false), ("ApiUrl", OctopusImportChoiceReason.AddedInOctopus, false), ("ApiUrl", OctopusImportChoiceReason.AddedInOctopus, false)], ignoreOrder: true, customMessage: "the preview must offer the three deleted and the five added children (both ApiUrl namesakes), with their reasons and defaults");
        import.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: string.Join("; ", import.Confirm.Data.Session.Result.Diagnostics.Select(d => $"{d.Code} {d.Message}")));

        var executed = await DeployOnChannelAsync("Canary", "3.0.1");

        executed.Select(e => e.Step).ShouldBe(["Build", "Migrate", "Deploy", "Smoke"], customMessage: "the deployed release must run every step in Octopus order, the inserted one included; inspect deployment_step.step_order for the project's process");
        executed.ShouldAllBe(e => e.Script.Contains("api=https://test"), customMessage: "both ApiUrl namesakes must be added and the one scoped to Server TEST must win; inspect the variable and variable_scope rows of the project's set");
        (await ChannelNamesAsync()).ShouldBe(["Default", "Canary"], ignoreOrder: true, customMessage: "the unticked channel deleted in Squid must stay deleted");
        (await PhaseNamesAsync()).ShouldBe(["TEST"], customMessage: "the unticked phase must not be added");
        (await VariableNamesAsync()).ShouldBe(["Greeting", "ApiUrl", "ApiUrl"], ignoreOrder: true, customMessage: "the unticked variable deleted in Squid must stay deleted, both ticked namesakes added");

        var before = await CountsAsync();
        var last = await ImportAsync(octopus, plan => plan);

        last.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
        last.PreviewPlan.Summary.IsUpToDate.ShouldBeTrue(customMessage: "only declined offers are left, and they write nothing");
        last.PreviewPlan.Summary.Choices.Sum(c => c.Total).ShouldBe(3, customMessage: "declined choices are offered again on every import (Hotfix, STAGING, Farewell)");
        (await CountsAsync()).ShouldBe(before, customMessage: "a last import with only declined offers must write nothing");
    }

    // ── Octopus ──────────────────────────────────────────────────────────

    // One run-on-server script step per name, ids derived from the name, in this order.
    private static void Process(StubOctopusServer octopus, params string[] steps)
        => octopus.Resources["projects/Projects-1/deploymentprocesses"] = new Dictionary<string, object> { ["Id"] = "deploymentprocess-Projects-1", ["ProjectId"] = "Projects-1", ["Steps"] = steps.Select(Step).ToArray() };

    private static object Step(string name)
        => new
        {
            Id = $"Step-{name}", Name = name, Condition = "Success", StartTrigger = "StartAfterPrevious", PackageRequirement = "LetOctopusDecide", Properties = new Dictionary<string, string>(),
            Actions = new[] { new { Id = $"Action-{name}", Name = name, ActionType = "Octopus.Script", Environments = Array.Empty<string>(), ExcludedEnvironments = Array.Empty<string>(), Channels = Array.Empty<string>(), Packages = Array.Empty<object>(), Properties = new Dictionary<string, string> { ["Octopus.Action.Script.ScriptSource"] = "Inline", ["Octopus.Action.Script.Syntax"] = "Bash", ["Octopus.Action.Script.ScriptBody"] = $"echo step-{name} api=#{{ApiUrl}}", ["Octopus.Action.RunOnServer"] = "true" } } }
        };

    private static readonly OctopusVariable Greeting = new("Variable-Greeting", "Greeting", "hello", null);
    private static readonly OctopusVariable Farewell = new("Variable-Farewell", "Farewell", "bye", null);
    private static readonly OctopusVariable ApiUrlEverywhere = new("Variable-ApiUrl-All", "ApiUrl", "https://all", null);
    private static readonly OctopusVariable ApiUrlOnTest = new("Variable-ApiUrl-Test", "ApiUrl", "https://test", "Environments-1");

    private sealed record OctopusVariable(string Id, string Name, string Value, string EnvironmentId);

    private static void Variables(StubOctopusServer octopus, params OctopusVariable[] variables)
        => octopus.Resources["projects/Projects-1/variables"] = new Dictionary<string, object> { ["Id"] = "variableset-Projects-1", ["OwnerId"] = "Projects-1", ["Variables"] = variables.Select(VariableDocument).ToArray() };

    private static Dictionary<string, object> VariableDocument(OctopusVariable variable)
        => new() { ["Id"] = variable.Id, ["Name"] = variable.Name, ["Value"] = variable.Value, ["Type"] = "String", ["Scope"] = variable.EnvironmentId == null ? new Dictionary<string, object>() : new Dictionary<string, object> { ["Environment"] = new[] { variable.EnvironmentId } } };

    private static void Channels(StubOctopusServer octopus, params (string Id, string Name)[] channels)
    {
        var all = new List<Dictionary<string, object>> { new() { ["Id"] = "Channels-1", ["Name"] = "Default", ["Slug"] = "default", ["ProjectId"] = "Projects-1", ["IsDefault"] = true } };

        all.AddRange(channels.Select(c => new Dictionary<string, object> { ["Id"] = c.Id, ["Name"] = c.Name, ["Slug"] = c.Name.ToLowerInvariant(), ["ProjectId"] = "Projects-1", ["IsDefault"] = false }));
        octopus.Resources["projects/Projects-1/channels"] = all;

        if (channels.Any(c => c.Id == "Channels-3")) ReleaseOnCanary(octopus);
    }

    private static void ReleaseOnCanary(StubOctopusServer octopus)
    {
        octopus.Release("Projects-1", "Releases-3", "3.0.0").Deployment("Projects-1", "Deployments-3", "Releases-3", "ServerTasks-3");
        octopus.Releases["Projects-1"].Single(r => (string)r["Id"] == "Releases-3")["ChannelId"] = "Channels-3";
        octopus.Deployments["Projects-1"].Single(d => (string)d["Id"] == "Deployments-3")["ChannelId"] = "Channels-3";
    }

    private static void Phases(StubOctopusServer octopus, params string[] names)
        => octopus.Resources["lifecycles"] = new List<Dictionary<string, object>> { new() { ["Id"] = "Lifecycles-1", ["Name"] = "Server lifecycle", ["Slug"] = "server-lifecycle", ["Phases"] = names.Select((name, index) => new Dictionary<string, object> { ["Id"] = $"Phases-{index + 1}", ["Name"] = name, ["AutomaticDeploymentTargets"] = new[] { "Environments-1" } }).ToArray() } };

    // ── Import, as the UI drives it ───────────────────────────────────────

    private Task<OctopusImportFlowResult> ImportAsync(StubOctopusServer octopus, Func<OctopusImportPreviewPlanDto, OctopusImportPreviewPlanDto> choose)
        => _fixture.Run<IMediator, OctopusImportFlowResult>(async mediator =>
        {
            var sessionId = (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId, octopus.BaseUri, ApiKey)).Data.Session.SessionId;
            var fetch = await OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey);
            (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, sessionId)).Code.ShouldBe(HttpStatusCode.OK);
            var preview = (await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId)).Data.PreviewPlan;
            var validate = await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, choose(preview));

            validate.Code.ShouldBe(HttpStatusCode.OK, customMessage: OctopusImportHandlerFlow.DescribeBlockers(validate.Data.Validation.Diagnostics));

            return new OctopusImportFlowResult(sessionId, fetch, preview, await OctopusImportHandlerFlow.ConfirmAsync(mediator, SpaceId, sessionId));
        });

    private static OctopusImportPreviewPlanDto Tick(OctopusImportPreviewPlanDto plan, string sourceId)
    {
        plan.Resources.Single(r => string.Equals(r.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)).Choice.Selected = true;

        return plan;
    }

    // ── Squid ────────────────────────────────────────────────────────────

    private Task DeleteStepInSquidAsync(string name)
        => _fixture.Run<SquidDbContext, IDeploymentStepService>(async (db, steps) =>
        {
            var id = await db.Set<DeploymentStep>().AsNoTracking().Where(s => s.Name == name).Select(s => s.Id).SingleAsync();

            await steps.DeleteDeploymentStepsAsync(new DeleteDeploymentStepCommand { Ids = [id] }, CancellationToken.None);
        });

    private Task DeleteChannelInSquidAsync(string name)
        => _fixture.Run<SquidDbContext>(db => db.Set<Channel>().Where(c => c.Name == name).ExecuteDeleteAsync());

    private Task DeleteVariableInSquidAsync(string name)
        => _fixture.Run<SquidDbContext>(db => db.Set<Variable>().Where(v => v.Name == name).ExecuteDeleteAsync());

    // A new release on the channel, created and deployed in separate scopes as the API and the background runner do; returns the steps
    // whose scripts ran, in the order they ran, with the script each ran.
    private async Task<List<(string Step, string Script)>> DeployOnChannelAsync(string channelName, string version)
    {
        var taskId = 0;
        _fixture.ExecutionCapture.CapturedRequests.Clear();

        await _fixture.Run<SquidDbContext, IReleaseService, IDeploymentService>(async (db, releases, deployments) =>
        {
            var channel = await db.Set<Channel>().AsNoTracking().SingleAsync(c => c.Name == channelName);
            var environment = await db.Set<SquidEnvironment>().AsNoTracking().SingleAsync(e => e.Name == "Server TEST");
            var release = await releases.CreateReleaseAsync(new CreateReleaseCommand { SpaceId = SpaceId, ProjectId = channel.ProjectId, ChannelId = channel.Id, Version = version });

            taskId = (await deployments.CreateDeploymentAsync(new CreateDeploymentCommand { SpaceId = SpaceId, ReleaseId = release.Release.Id, EnvironmentId = environment.Id, Name = $"Deploy {version}" })).TaskId;
        });

        await _fixture.Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(taskId, CancellationToken.None));

        (await _fixture.Run<SquidDbContext, string>(db => db.Set<ServerTask>().AsNoTracking().Where(t => t.Id == taskId).Select(t => t.State).SingleAsync())).ShouldBe(TaskState.Success, customMessage: $"deployment task {taskId} did not succeed; check its server_task_log rows");

        return _fixture.ExecutionCapture.CapturedRequests.Select(r => (new[] { "Build", "Migrate", "Deploy", "Smoke" }.Single(name => r.ScriptBody.Contains($"echo step-{name} ")), r.ScriptBody)).ToList();
    }

    private Task<List<string>> ChannelNamesAsync() => _fixture.Run<SquidDbContext, List<string>>(db => db.Set<Channel>().AsNoTracking().Select(c => c.Name).ToListAsync());

    private Task<List<string>> VariableNamesAsync() => _fixture.Run<SquidDbContext, List<string>>(db => db.Set<Variable>().AsNoTracking().Select(v => v.Name).ToListAsync());

    private Task<List<string>> PhaseNamesAsync() => _fixture.Run<SquidDbContext, List<string>>(db => db.Set<LifecyclePhase>().AsNoTracking().OrderBy(p => p.SortOrder).Select(p => p.Name).ToListAsync());

    private Task<string> CountsAsync()
        => _fixture.Run<SquidDbContext, string>(async db => $"{await db.Set<DeploymentStep>().CountAsync()}/{await db.Set<DeploymentAction>().CountAsync()}/{await db.Set<Channel>().CountAsync()}/{await db.Set<LifecyclePhase>().CountAsync()}/{await db.Set<Release>().CountAsync()}/{await db.Set<Deployment>().CountAsync()}/{await db.Set<Variable>().CountAsync()}/{await db.Set<OctopusImportSourceMap>().CountAsync()}");

    private Task EnsureSpaceAsync()
        => _fixture.Run<SquidDbContext>(async db =>
        {
            if (await db.Set<Space>().AnyAsync(s => s.Id == SpaceId)) return;

            db.Set<Space>().Add(new Space { Name = "Default", Slug = "default", IsDefault = true });
            await db.SaveChangesAsync();
        });

    /// <summary>The pipeline fixture plus what the mediator-driven import needs in a test host: the HTTP context accessor, loopback allowed for the stub, and open authorization.</summary>
    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusOptionalChildrenImportE2ETests>
    {
        protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
        {
            base.RegisterOverrides(builder, configuration);

            builder.RegisterType<HttpContextAccessor>().As<IHttpContextAccessor>().SingleInstance();
            builder.RegisterInstance(new OctopusRestOptions { AllowLoopback = true });

            var authorization = new Moq.Mock<IAuthorizationService>();
            authorization.Setup(a => a.EnsurePermissionAsync(Moq.It.IsAny<PermissionCheckRequest>(), Moq.It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            builder.RegisterInstance(authorization.Object).As<IAuthorizationService>();
        }
    }
}
