using System.Text.Json;
using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.DataSeeding;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Teams;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Interruption;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Account;
using Shouldly;
using Xunit;
using Mediator.Net;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// Fidelity: HIGH for the approval path — the REAL deployment pipeline suspends on a manual intervention
/// whose responsible team has NO members (what an Octopus import produces), the REAL mediator pipeline
/// (space scope + authorization middleware + SubmitInterruptionCommandHandler) authorizes submissions
/// against REAL roles/teams in Postgres, and the REAL pipeline resumes. Script execution is captured
/// (CapturingExecutionStrategy) because the subject is approval routing, not script transport.
/// </summary>
[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class ManualInterventionAdministratorOverrideE2ETests : IClassFixture<DeploymentPipelineFixture<ManualInterventionAdministratorOverrideE2ETests>>
{
    private const int SpaceId = 1;

    private readonly DeploymentPipelineFixture<ManualInterventionAdministratorOverrideE2ETests> _fixture;

    public ManualInterventionAdministratorOverrideE2ETests(KindClusterFixture cluster, DeploymentPipelineFixture<ManualInterventionAdministratorOverrideE2ETests> fixture)
    {
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    [Fact]
    public async Task EmptyApprovalTeam_NonMemberNonAdminIsRejected_SpaceOwnerOverrideApproves_AndDeploymentProceeds()
    {
        ExecutionCapture.Clear();

        var users = UniqueUsers();
        var serverTaskId = await SeedPipelineWithEmptyApprovalTeamAsync(users);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await RunPipelineAsync(serverTaskId, cts.Token);
        var interruption = await WaitForPendingInterruptionAsync(serverTaskId, cts.Token);

        var rejection = await Should.ThrowAsync<InvalidOperationException>(() => SubmitAsAsync(users.Deployer, interruption.Id, "Proceed"), customMessage: "a non-member without the override permission must not approve an empty team's intervention");
        rejection.Message.ShouldContain(nameof(Permission.InterruptionSubmitOverride));
        (await GetInterruptionAsync(interruption.Id)).Resolution.ShouldBeNull("the rejected submission must not resolve the interruption");

        await SubmitAsAsync(users.SpaceOwner, interruption.Id, "Proceed");
        await RunPipelineAsync(serverTaskId, cts.Token);

        var resolved = await GetInterruptionAsync(interruption.Id);
        resolved.Resolution.ShouldBe("Proceed");
        resolved.AdministratorOverrideUserId.ShouldBe(users.SpaceOwner);

        await AssertTaskStateAsync(serverTaskId, TaskState.Success);
        ExecutionCapture.CapturedRequests.Select(r => r.ScriptBody).ShouldContain(s => s.Contains("after-approval"), "the step after the approval must run once the override approved it");
        (await TaskLogContainsAsync(serverTaskId, "resolved via administrator override by")).ShouldBeTrue("the task activity log must record that the approval came from an administrator override");
    }

    [Fact]
    public async Task EmptyApprovalTeam_SystemAdministratorOverrideAborts_AndDeploymentFails()
    {
        ExecutionCapture.Clear();

        var users = UniqueUsers();
        var serverTaskId = await SeedPipelineWithEmptyApprovalTeamAsync(users);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await RunPipelineAsync(serverTaskId, cts.Token);
        var interruption = await WaitForPendingInterruptionAsync(serverTaskId, cts.Token);

        await SubmitAsAsync(users.SystemAdmin, interruption.Id, "Abort");
        await RunPipelineAsync(serverTaskId, cts.Token);

        (await GetInterruptionAsync(interruption.Id)).AdministratorOverrideUserId.ShouldBe(users.SystemAdmin);
        await AssertTaskStateAsync(serverTaskId, TaskState.Failed);
        ExecutionCapture.CapturedRequests.Select(r => r.ScriptBody).ShouldNotContain(s => s.Contains("after-approval"));
    }

    // ── Pipeline / mediator drivers ────────────────────────────────────────────────────────────

    private async Task RunPipelineAsync(int serverTaskId, CancellationToken ct)
    {
        try
        {
            await _fixture.Run<IDeploymentTaskExecutor>(async executor => await executor.ProcessAsync(serverTaskId, ct).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Squid.Core.Services.DeploymentExecution.Exceptions.DeploymentAbortedException)
        {
            // Expected after an Abort decision — the resumed pipeline records the failure and re-throws.
        }
    }

    private async Task<DeploymentInterruption> WaitForPendingInterruptionAsync(int serverTaskId, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var pending = await _fixture.Run<IDeploymentInterruptionService, List<DeploymentInterruption>>(async service => await service.GetPendingInterruptionsAsync(serverTaskId, ct).ConfigureAwait(false)).ConfigureAwait(false);

            if (pending.Count > 0) return pending.Single();

            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    private async Task SubmitAsAsync(int userId, int interruptionId, string decision)
    {
        await using var scope = _fixture.LifetimeScope.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance<ICurrentUser>(new FixedCurrentUser(userId)).SingleInstance();
            builder.RegisterType<HttpContextAccessor>().As<IHttpContextAccessor>().SingleInstance();
        });

        var command = new SubmitInterruptionCommand { SpaceId = SpaceId, InterruptionId = interruptionId, Values = new Dictionary<string, string> { ["Result"] = decision } };

        await scope.Resolve<IMediator>().SendAsync<SubmitInterruptionCommand, SubmitInterruptionResponse>(command).ConfigureAwait(false);
    }

    private async Task<DeploymentInterruption> GetInterruptionAsync(int interruptionId)
        => await _fixture.Run<SquidDbContext, DeploymentInterruption>(async db => await db.Set<DeploymentInterruption>().AsNoTracking().SingleAsync(i => i.Id == interruptionId).ConfigureAwait(false)).ConfigureAwait(false);

    private async Task<bool> TaskLogContainsAsync(int serverTaskId, string text)
        => await _fixture.Run<SquidDbContext, bool>(async db => await db.Set<ServerTaskLog>().AsNoTracking().AnyAsync(l => l.ServerTaskId == serverTaskId && l.MessageText.Contains(text)).ConfigureAwait(false)).ConfigureAwait(false);

    private async Task AssertTaskStateAsync(int serverTaskId, string expectedState)
    {
        await _fixture.Run<IServerTaskDataProvider>(async provider =>
        {
            var task = await provider.GetServerTaskByIdAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);

            task.State.ShouldBe(expectedState, $"Expected task state '{expectedState}' but was '{task.State}' — inspect the server_task_log rows for task {serverTaskId}");
        }).ConfigureAwait(false);
    }

    // ── Seeding ─────────────────────────────────────────────────────────────────────────────────

    private static (int SpaceOwner, int Deployer, int SystemAdmin) UniqueUsers()
    {
        var seed = Random.Shared.Next(100_000, 900_000);

        return (seed + 1, seed + 2, seed + 3);
    }

    private async Task<int> SeedPipelineWithEmptyApprovalTeamAsync((int SpaceOwner, int Deployer, int SystemAdmin) users)
    {
        var serverTaskId = 0;

        await _fixture.Run<ILifetimeScope>(async scope =>
        {
            var teams = scope.Resolve<ITeamDataProvider>();
            var roles = scope.Resolve<IUserRoleDataProvider>();
            var scopedRoles = scope.Resolve<IScopedUserRoleDataProvider>();

            await GrantRoleAsync(teams, roles, scopedRoles, users.SpaceOwner, BuiltInRoles.SpaceOwner, SpaceId).ConfigureAwait(false);
            await GrantRoleAsync(teams, roles, scopedRoles, users.Deployer, BuiltInRoles.ProjectDeployer, SpaceId).ConfigureAwait(false);
            await GrantRoleAsync(teams, roles, scopedRoles, users.SystemAdmin, BuiltInRoles.SystemAdministrator, null).ConfigureAwait(false);

            var approvers = new Team { Name = $"Imported approvers {Guid.NewGuid():N}", SpaceId = SpaceId };
            await teams.AddAsync(approvers).ConfigureAwait(false);

            serverTaskId = await SeedDeploymentAsync(scope.Resolve<IRepository>(), scope.Resolve<IUnitOfWork>(), approvers.Id).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return serverTaskId;
    }

    private static async Task GrantRoleAsync(ITeamDataProvider teams, IUserRoleDataProvider roles, IScopedUserRoleDataProvider scopedRoles, int userId, BuiltInRoleDefinition definition, int? spaceId)
    {
        var team = new Team { Name = $"e2e-team-of-{userId}", SpaceId = spaceId ?? 0 };
        await teams.AddAsync(team).ConfigureAwait(false);
        await teams.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = userId }).ConfigureAwait(false);

        var role = await roles.GetByNameAsync(definition.Name).ConfigureAwait(false);

        if (role == null)
        {
            role = new UserRole { Name = definition.Name, IsBuiltIn = true };
            await roles.AddAsync(role).ConfigureAwait(false);
            await roles.SetPermissionsAsync(role.Id, definition.Permissions.Select(p => p.ToString()).ToList()).ConfigureAwait(false);
        }

        await scopedRoles.AddAsync(new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id, SpaceId = spaceId }).ConfigureAwait(false);
    }

    private static async Task<int> SeedDeploymentAsync(IRepository repository, IUnitOfWork unitOfWork, int approvalTeamId)
    {
        var builder = new TestDataBuilder(repository, unitOfWork);
        var suffix = Guid.NewGuid().ToString("N")[..6];

        var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
        var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
        await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

        var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
        await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

        var approvalStep = await builder.CreateDeploymentStepAsync(process.Id, 1, "Approval", "Manual", "Success").ConfigureAwait(false);
        await builder.CreateStepPropertiesAsync(approvalStep.Id, ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);
        var approval = await builder.CreateDeploymentActionAsync(approvalStep.Id, 1, "Approve release", actionType: "Squid.Manual").ConfigureAwait(false);
        await builder.CreateActionMachineRolesAsync(approval.Id, "k8s").ConfigureAwait(false);
        await builder.CreateActionPropertiesAsync(approval.Id, (SpecialVariables.Action.ManualInstructions, "Approve the imported release"), (SpecialVariables.Action.ManualResponsibleTeamIds, approvalTeamId.ToString())).ConfigureAwait(false);

        var postStep = await builder.CreateDeploymentStepAsync(process.Id, 2, "After Approval", "Action", "Success").ConfigureAwait(false);
        await builder.CreateStepPropertiesAsync(postStep.Id, ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);
        var post = await builder.CreateDeploymentActionAsync(postStep.Id, 1, "Post Script", actionType: "Squid.Script").ConfigureAwait(false);
        await builder.CreateActionMachineRolesAsync(post.Id, "k8s").ConfigureAwait(false);
        await builder.CreateActionPropertiesAsync(post.Id, ("Squid.Action.Script.ScriptBody", "echo 'after-approval'"), ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

        var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
        var environment = await builder.CreateEnvironmentAsync($"E2E Override Env {suffix}").ConfigureAwait(false);

        var endpointJson = JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesApi", ClusterUrl = "https://localhost:6443", SkipTlsVerification = "True", Namespace = "default", ResourceReferences = new[] { new { Type = (int)EndpointResourceType.AuthenticationAccount, ResourceId = 1 } } });

        await repository.InsertAsync(new Machine { Name = $"E2E Override Target {suffix}", IsDisabled = false, Roles = "k8s", EnvironmentIds = environment.Id.ToString(), Endpoint = endpointJson, SpaceId = SpaceId, Slug = $"e2e-override-target-{suffix}" }).ConfigureAwait(false);
        await repository.InsertAsync(new DeploymentAccount { SpaceId = SpaceId, Name = $"E2E Override Account {suffix}", Slug = $"e2e-override-account-{suffix}", AccountType = AccountType.Token, Credentials = DeploymentAccountCredentialsConverter.Serialize(new TokenCredentials { Token = "e2e-test-token" }) }).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

        var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

        var deployment = new Deployment { Name = $"E2E Override Deployment {suffix}", SpaceId = SpaceId, ChannelId = channel.Id, ProjectId = project.Id, ReleaseId = release.Id, EnvironmentId = environment.Id, DeployedBy = 1, CreatedDate = DateTimeOffset.UtcNow, Json = string.Empty };
        await repository.InsertAsync(deployment).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

        var serverTask = new ServerTask { Name = $"E2E Override Task {suffix}", Description = "E2E administrator override", QueueTime = DateTimeOffset.UtcNow, State = TaskState.Pending, ServerTaskType = "Deploy", ProjectId = project.Id, EnvironmentId = environment.Id, SpaceId = SpaceId, LastModifiedDate = DateTimeOffset.UtcNow, BusinessProcessState = "Queued", StateOrder = 1, Weight = 1, BatchId = 0, JSON = string.Empty, HasWarningsOrErrors = false, ServerNodeId = Guid.NewGuid(), DurationSeconds = 0, DataVersion = Array.Empty<byte>() };
        await repository.InsertAsync(serverTask).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

        deployment.TaskId = serverTask.Id;
        await repository.UpdateAsync(deployment).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

        return serverTask.Id;
    }

    private sealed class FixedCurrentUser(int id) : ICurrentUser
    {
        public int? Id => id;
        public string Name => $"e2e-user-{id}";
        public bool IsInternal => false;
    }
}
