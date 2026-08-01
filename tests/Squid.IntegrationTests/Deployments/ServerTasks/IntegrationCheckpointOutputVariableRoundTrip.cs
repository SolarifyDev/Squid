using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Security;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Variable;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.IntegrationTests.Deployments.ServerTasks;

/// <summary>
/// Real-database round-trip of the checkpoint output-variable contract: production serializer
/// → real <c>jsonb</c> column → production resume phase, with the DI-resolved encryption
/// service (real AES-GCM, real master key) rather than a hand-built one.
///
/// <para><b>The regression this locks</b>: the checkpoint used to be selected out of the
/// variable list with <c>Name.StartsWith("Squid.Action.")</c>, which matches none of the names
/// <see cref="SpecialVariables.Output"/> actually mints (<c>Squid.Action[step].Output.name</c> —
/// bracket, not dot). Every resumed deployment silently lost its output variables. The unit
/// suite missed it because it asserted against invented names that satisfied the buggy
/// predicate; this test uses only names produced by the SSOT and drives the real column.</para>
/// </summary>
public class IntegrationCheckpointOutputVariableRoundTrip : ServerTaskFixtureBase
{
    [Fact]
    public async Task CapturedOutputVariables_SurviveTheDatabaseRoundTrip_AndAreRestoredOnResume()
    {
        var taskId = await SeedExecutingTaskAsync();

        var stepQualified = SpecialVariables.Output.Variable("Deploy Web", "SiteUrl");
        var machineQualified = SpecialVariables.Output.MachineVariable("Deploy Web", "web-01", "SiteUrl");
        const string bareAlias = "SiteUrl";
        var secretName = SpecialVariables.Output.Variable("Deploy Web", "ApiKey");

        // ── Persist through the production serializer into the real column ──
        await Run<IDeploymentCheckpointService, IVariableEncryptionService>(async (checkpointService, encryption) =>
        {
            var json = CheckpointOutputVariableSerializer.Serialize(
            [
                new VariableDto { Name = stepQualified, Value = "https://web.test" },
                new VariableDto { Name = machineQualified, Value = "https://web-01.test" },
                new VariableDto { Name = bareAlias, Value = "https://web.test" },
                new VariableDto { Name = secretName, Value = "super-secret-key", IsSensitive = true }
            ], encryption, taskId);

            json.ShouldNotBeNull("the production serializer MUST produce checkpoint JSON for captured output variables");

            await checkpointService.SaveAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = taskId,
                DeploymentId = 1,
                LastCompletedBatchIndex = 0,
                FailureEncountered = false,
                OutputVariablesJson = json
            });
        });

        // ── The stored column must not leak the secret ──
        await Run<IRepository>(async repository =>
        {
            var row = await repository.QueryNoTracking<DeploymentExecutionCheckpoint>(c => c.ServerTaskId == taskId)
                .FirstOrDefaultAsync();

            row.ShouldNotBeNull();
            row.OutputVariablesJson.ShouldNotBeNull(
                customMessage: "output variables MUST reach the database — a null column here is the silent-loss regression");
            row.OutputVariablesJson.ShouldNotContain("super-secret-key",
                customMessage: "a sensitive output variable MUST be encrypted in the checkpoint column");
            row.OutputVariablesJson.ShouldContain("https://web.test",
                customMessage: "non-sensitive output variables stay plaintext for operator inspection");
        });

        // ── Resume through the production phase ──
        await Run<IDeploymentCheckpointService, IVariableEncryptionService>(async (checkpointService, encryption) =>
        {
            var ctx = new DeploymentTaskContext
            {
                ServerTaskId = taskId,
                Task = new ServerTaskEntity { Id = taskId },
                Deployment = new Deployment { Id = 1 },
                Release = new ReleaseEntity { Id = 1, Version = "1.0.0" },
                Variables = new List<VariableDto>(),
                SelectedPackages = new List<ReleaseSelectedPackage>()
            };

            await new ResumeCheckpointPhase(checkpointService, encryption).ExecuteAsync(ctx, CancellationToken.None);

            var names = ctx.RestoredOutputVariables.Select(v => v.Name).ToList();

            names.ShouldContain(stepQualified);
            names.ShouldContain(machineQualified);
            names.ShouldContain(bareAlias,
                customMessage: "the un-qualified alias MUST survive too — scripts reference it as #{SiteUrl}");

            ctx.RestoredOutputVariables.Single(v => v.Name == secretName).Value
                .ShouldBe("super-secret-key", "resume MUST decrypt sensitive values back to plaintext");
        });
    }

    private async Task<int> SeedExecutingTaskAsync()
    {
        var taskId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var task = new ServerTaskEntity
            {
                Name = "Checkpoint Output Variable RoundTrip",
                Description = "Task for checkpoint output-variable round-trip test",
                QueueTime = DateTimeOffset.UtcNow,
                State = TaskState.Executing,
                StartTime = DateTimeOffset.UtcNow,
                ServerTaskType = "Deploy",
                ProjectId = 1,
                EnvironmentId = 1,
                SpaceId = 1,
                LastModifiedDate = DateTimeOffset.UtcNow,
                BusinessProcessState = "Executing",
                StateOrder = 1,
                Weight = 1,
                BatchId = 0,
                JSON = string.Empty,
                HasWarningsOrErrors = false,
                ServerNodeId = Guid.NewGuid(),
                DurationSeconds = 0,
                DataVersion = Guid.NewGuid().ToByteArray()
            };

            await repository.InsertAsync(task);
            await unitOfWork.SaveChangesAsync();
            taskId = task.Id;
        });

        return taskId;
    }
}
