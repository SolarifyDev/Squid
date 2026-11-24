using Autofac;
using Halibut;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;
using Machine = Squid.Core.Persistence.Entities.Deployments.Machine;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Resuming a paused batch whose parallel steps (or parallel targets of one step) all use guided failure looks up each
/// one's resolved interruption at the same moment, on the run's single scoped DbContext. Without a gate the second lookup
/// throws "A second operation was started on this context instance". Fidelity: high (real pipeline, real Postgres
/// checkpoint + interruptions; only the agent RPC is faked).
/// </summary>
public class IntegrationParallelGuidedFailureResume : DeploymentFixtureBase
{
    private const string Step2Marker = "step-two-marker";
    private const string Step3Marker = "step-three-marker";

    [Fact]
    public async Task ParallelSteps_AllGuidedFailed_RetryAndIgnore_ResumeAppliesEveryResolution()
    {
        var seeded = await SeedTwoParallelGuidedStepsAsync().ConfigureAwait(false);
        var agent = new FailThenSucceedAgent();

        await RunPipelineAsync(seeded.TaskId, agent).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Paused).ConfigureAwait(false);

        await EnsureInterruptionsAsync(seeded.TaskId, new[] { ("Run script", "Run script"), ("Run script two", "Run script two"), ("Run script three", "Run script three") }).ConfigureAwait(false);
        await ResolveAsync(seeded.TaskId, "Run script", "Retry").ConfigureAwait(false);
        await ResolveAsync(seeded.TaskId, "Run script two", "Ignore").ConfigureAwait(false);
        await ResolveAsync(seeded.TaskId, "Run script three", "Retry").ConfigureAwait(false);

        agent.Succeed = true;
        var step2StartsBeforeResume = agent.Starts(Step2Marker);
        var step3StartsBeforeResume = agent.Starts(Step3Marker);
        var step1StartsBeforeResume = agent.StartsOfStep1;

        await RunPipelineAsync(seeded.TaskId, agent, resume: true).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Success).ConfigureAwait(false);
        agent.StartsOfStep1.ShouldBe(step1StartsBeforeResume + 1, "step 1 was resolved Retry: it must run again on resume");
        agent.Starts(Step2Marker).ShouldBe(step2StartsBeforeResume, "step 2 was resolved Ignore: it must not run again");
        agent.Starts(Step3Marker).ShouldBe(step3StartsBeforeResume + 1, "step 3 was resolved Retry: it must run again on resume");
    }

    [Fact]
    public async Task ParallelTargets_BothGuidedFailed_Retry_ResumeRerunsBothTargets()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true, machineCount: 2).ConfigureAwait(false);
        await EnableGuidedFailureAsync(seeded.TaskId).ConfigureAwait(false);
        var agent = new FailThenSucceedAgent();

        await RunPipelineAsync(seeded.TaskId, agent).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Paused).ConfigureAwait(false);

        var machineNames = await LoadMachineNamesAsync().ConfigureAwait(false);
        await EnsureInterruptionsAsync(seeded.TaskId, machineNames.Select(name => ("Run script", "Run script", name)).ToArray()).ConfigureAwait(false);

        foreach (var name in machineNames)
            await ResolveAsync(seeded.TaskId, "Run script", "Retry", name).ConfigureAwait(false);

        agent.Succeed = true;
        var startsBeforeResume = agent.StartsOfStep1;

        await RunPipelineAsync(seeded.TaskId, agent, resume: true).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Success).ConfigureAwait(false);
        (agent.StartsOfStep1 - startsBeforeResume).ShouldBe(2, "both targets were resolved Retry: each must run again on resume");
    }

    private async Task<SeededDeployment> SeedTwoParallelGuidedStepsAsync()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);
            var step1 = await repository.Query<DeploymentStep>(s => s.Id == seeded.DispatchSlot.StepId).FirstAsync().ConfigureAwait(false);

            foreach (var (order, name, marker) in new[] { (2, "Run script two", Step2Marker), (3, "Run script three", Step3Marker) })
            {
                var step = await builder.CreateDeploymentStepAsync(step1.ProcessId, order, name, isRequired: true).ConfigureAwait(false);
                step.StartTrigger = "StartWithPrevious";
                await repository.UpdateAsync(step).ConfigureAwait(false);
                await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

                await builder.CreateStepPropertiesAsync(step.Id, ("Squid.Action.TargetRoles", "web")).ConfigureAwait(false);
                var action = await builder.CreateDeploymentActionAsync(step.Id, 1, name, actionType: SpecialVariables.ActionTypes.Script).ConfigureAwait(false);
                await builder.CreateActionMachineRolesAsync(action.Id, "web").ConfigureAwait(false);
                await builder.CreateActionPropertiesAsync(action.Id, ("Squid.Action.Script.ScriptBody", $"echo {marker}"), ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        await EnableGuidedFailureAsync(seeded.TaskId).ConfigureAwait(false);

        return seeded;
    }

    private Task EnableGuidedFailureAsync(int taskId)
        => Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var task = await repository.QueryNoTracking<ServerTask>(t => t.Id == taskId).FirstAsync().ConfigureAwait(false);
            var environment = await repository.Query<Environment>(e => e.Id == task.EnvironmentId).FirstAsync().ConfigureAwait(false);

            environment.UseGuidedFailure = true;
            await repository.UpdateAsync(environment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        });

    private Task<List<string>> LoadMachineNamesAsync()
        => Run<IRepository, List<string>>(async repository =>
            await repository.QueryNoTracking<Machine>(m => m.Name.StartsWith("Transient Pause Agent")).Select(m => m.Name).ToListAsync().ConfigureAwait(false));

    // The first failing step suspends the run and stops its siblings, which may not have reached their own guided-failure prompt.
    // The operator sees whichever prompts were raised; this raises the rest so every step/target has an interruption to resolve,
    // which is exactly the state a resume of a fully-failed parallel batch finds.
    private Task EnsureInterruptionsAsync(int taskId, (string Step, string Action)[] expected)
        => EnsureInterruptionsAsync(taskId, expected.Select(e => (e.Step, e.Action, (string)null)).ToArray());

    private Task EnsureInterruptionsAsync(int taskId, (string Step, string Action, string Machine)[] expected)
        => Run<IDeploymentInterruptionService, IRepository>(async (service, repository) =>
        {
            var deploymentId = (await repository.QueryNoTracking<DeploymentExecutionCheckpoint>(c => c.ServerTaskId == taskId).FirstAsync().ConfigureAwait(false)).DeploymentId;
            var machine = (await repository.QueryNoTracking<Machine>(m => m.Name.StartsWith("Transient Pause Agent")).FirstAsync().ConfigureAwait(false)).Name;
            var existing = await repository.QueryNoTracking<DeploymentInterruption>(i => i.ServerTaskId == taskId).ToListAsync().ConfigureAwait(false);

            foreach (var (step, action, machineName) in expected)
            {
                var targetMachine = machineName ?? machine;

                if (existing.Any(i => i.StepName == step && i.ActionName == action && i.MachineName == targetMachine)) continue;

                await service.CreateInterruptionAsync(new CreateInterruptionRequest
                {
                    ServerTaskId = taskId, DeploymentId = deploymentId, InterruptionType = InterruptionType.GuidedFailure,
                    StepName = step, ActionName = action, MachineName = targetMachine, ErrorMessage = "scripted failure", SpaceId = 1,
                    Form = InterruptionFormBuilder.BuildGuidedFailureForm(step, action, targetMachine, "scripted failure")
                }, CancellationToken.None).ConfigureAwait(false);
            }
        });

    private Task ResolveAsync(int taskId, string stepName, string result, string machineName = null)
        => Run<IDeploymentInterruptionService>(async service =>
        {
            var pending = await service.GetPendingInterruptionsAsync(taskId, CancellationToken.None).ConfigureAwait(false);
            var match = pending.Single(i => i.StepName == stepName && (machineName == null || i.MachineName == machineName));

            await service.SubmitInterruptionAsync(match.Id, new Dictionary<string, string> { ["Result"] = result }, CancellationToken.None).ConfigureAwait(false);
        });

    private Task AssertStateAsync(int taskId, string expected)
        => Run<IServerTaskDataProvider>(async provider =>
        {
            var task = await provider.GetServerTaskByIdAsync(taskId, CancellationToken.None).ConfigureAwait(false);

            task.State.ShouldBe(expected);
        });

    private Task RunPipelineAsync(int taskId, IAsyncScriptService agent, bool resume = false)
        => Run<ILifetimeScope>(async scope =>
        {
            var runner = new DeploymentPipelineRunner(
                scope.Resolve<IEnumerable<IDeploymentPipelinePhase>>(),
                scope.Resolve<IDeploymentLifecycle>(),
                scope.Resolve<IDeploymentCompletionHandler>(),
                scope.Resolve<ITaskCancellationRegistry>(),
                scope.Resolve<IServerTaskDataProvider>(),
                scope.Resolve<ISquidBackgroundJobClient>())
            {
                DeploymentTimeout = TimeSpan.FromSeconds(60),
                TimeoutResumable = true
            };

            await runner.ProcessAsync(taskId, CancellationToken.None, resume).WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        }, extraRegistration: b => b.RegisterInstance(new FixedClientFactory(agent)).As<IHalibutClientFactory>().SingleInstance());

    // Every script fails until Succeed is set; starts are counted per script (step 2 carries a marker in its body).
    private sealed class FailThenSucceedAgent : IAsyncScriptService
    {
        private readonly object _gate = new();
        private int _step2Starts;
        private int _step3Starts;

        public bool Succeed { get; set; }
        public int StartsOfStep1 { get; private set; }

        public int Starts(string marker) => marker == Step2Marker ? _step2Starts : marker == Step3Marker ? _step3Starts : throw new ArgumentException(marker);

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command)
        {
            lock (_gate)
            {
                if (command.ScriptBody.Contains(Step2Marker, StringComparison.Ordinal)) _step2Starts++;
                else if (command.ScriptBody.Contains(Step3Marker, StringComparison.Ordinal)) _step3Starts++;
                else StartsOfStep1++;
            }

            return Task.FromResult(Resp(command.ScriptTicket, ProcessState.Running, 0));
        }

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request) => Task.FromResult(Resp(request.Ticket, ProcessState.Complete, Succeed ? 0 : 1));

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command) => Task.FromResult(Resp(command.Ticket, ProcessState.Complete, Succeed ? 0 : 1));

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command) => Task.FromResult(Resp(command.Ticket, ProcessState.Complete, 0));

        private static ScriptStatusResponse Resp(ScriptTicket ticket, ProcessState state, int exitCode) => new(ticket, state, exitCode, new List<ProcessOutput>(), 0);
    }

    private sealed class FixedClientFactory : IHalibutClientFactory
    {
        private readonly IAsyncScriptService _client;

        public FixedClientFactory(IAsyncScriptService client) => _client = client;

        public IAsyncScriptService CreateClient(ServiceEndPoint endpoint) => _client;

        public IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint) => new NoOpCapabilities();

        public IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint) => throw new NotSupportedException();

        private sealed class NoOpCapabilities : IAsyncCapabilitiesService
        {
            public Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request) => Task.FromResult(new CapabilitiesResponse());
        }
    }
}
