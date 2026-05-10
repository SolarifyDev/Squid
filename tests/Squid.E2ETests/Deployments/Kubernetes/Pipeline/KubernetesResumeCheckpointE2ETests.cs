using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.E2ETests.Helpers;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Variable;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;
using Microsoft.EntityFrameworkCore;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// E2E-2 — end-to-end verification of the checkpoint round-trip introduced
/// by 1.6.6 P0-3 (sensitive output variable encryption) and P0-5 (persist
/// retry). Unit tests at <c>CheckpointSensitiveVarEncryptionTests</c> +
/// <c>CheckpointPersistRetryTests</c> proved the logic in isolation; this
/// class proves it through the FULL pipeline against a REAL Postgres DB:
/// <list type="bullet">
///   <item>Sensitive output variables emitted by user scripts are encrypted
///         via <c>VariableEncryptionService</c> (V2 envelope) and persisted
///         to <c>DeploymentExecutionCheckpoint.OutputVariablesJson</c>.</item>
///   <item>The checkpoint JSON column physically does NOT contain the
///         plaintext value — direct DB query confirms.</item>
///   <item>Pre-fix plaintext checkpoints (legacy data planted in the DB)
///         resume cleanly via <c>IsValidEncryptedValue</c> passthrough —
///         in-flight upgrades from 1.6.5 → 1.6.6 don't break.</item>
///   <item>Multi-batch deploys persist incremental checkpoint state
///         (<c>LastCompletedBatchIndex</c> advances; output vars accumulate).</item>
///   <item>Resume from a planted checkpoint correctly skips already-completed
///         batches.</item>
/// </list>
///
/// <para><b>Why this E2E matters</b>: 1.6.6's checkpoint encryption is the
/// kind of fix that's perfectly correct in unit tests but silently broken in
/// production if the DI registration of <c>IVariableEncryptionService</c>
/// drifts, or if EF/JSON serialisation of the encrypted ciphertext escapes
/// special chars wrongly, or if backward-compat decode paths trip on a
/// real-world value. None of that surfaces in unit tests. This class is the
/// regression net.</para>
///
/// <para><b>Pattern</b>: <c>DeploymentPipelineFixture</c> (Pattern 2 in
/// CLAUDE.md). Runs the full pipeline against the real Postgres DB the
/// fixture provisions per test class. Output variables are simulated by
/// having <see cref="CapturingExecutionStrategy.ResultFactory"/> emit
/// <c>##squid[setVariable name='X' value='Y' sensitive='True']</c> log
/// lines that the pipeline's <c>ServiceMessageParser</c> parses into
/// real <see cref="VariableDto"/>s — same code path as a real agent.</para>
/// </summary>
[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class KubernetesResumeCheckpointE2ETests
    : IClassFixture<DeploymentPipelineFixture<KubernetesResumeCheckpointE2ETests>>
{
    private readonly KindClusterFixture _cluster;
    private readonly DeploymentPipelineFixture<KubernetesResumeCheckpointE2ETests> _fixture;

    public KubernetesResumeCheckpointE2ETests(
        KindClusterFixture cluster,
        DeploymentPipelineFixture<KubernetesResumeCheckpointE2ETests> fixture)
    {
        _cluster = cluster;
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    // ────────────────────────────────────────────────────────────────────────
    // 1. P0-3 ENCRYPTION ROUND-TRIP — the headline verification
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Encryption_SensitiveOutputVar_PersistedAsCiphertextInCheckpointJson()
    {
        // The user-script emit. The ##squid[setVariable] line will be parsed
        // by ServiceMessageParser into a sensitive VariableDto; ApplyBatchResults
        // routes it through OutputVariableMerger; PersistCheckpointAsync then
        // calls SerializeOutputVariables which P0-3 now encrypts.
        const string sensitiveValue = "secret-api-key-2026-do-not-leak";
        const string varName = "Squid.Action.DeploySecret.ApiToken";

        SetCaptureFactoryToEmit(varName, sensitiveValue, sensitive: true);

        var serverTaskId = await SeedSingleStepDeployAsync();
        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync(serverTaskId);

        // Direct DB inspection — query the checkpoint row and inspect JSON.
        var json = await GetCheckpointJsonAsync(serverTaskId);

        json.ShouldNotBeNullOrEmpty(
            customMessage: "Checkpoint OutputVariablesJson MUST be populated when output vars are emitted. " +
                          "Empty implies SerializeOutputVariables filtered them out (check Squid.Action. prefix match).");

        json.ShouldNotContain(sensitiveValue,
            customMessage: "P0-3 SECURITY REGRESSION: plaintext sensitive value found in checkpoint JSON. " +
                          "DBA / DB-read-access leaks the secret directly. Verify EncryptIfSensitive is called " +
                          "on every IsSensitive=true var before serialization.");

        json.ShouldContain("SQUID_ENCRYPTED",
            customMessage: "Encrypted values MUST carry the SQUID_ENCRYPTED prefix. If absent, encryption was skipped " +
                          "(check IVariableEncryptionService DI registration + master key configuration).");
    }

    [Fact]
    public async Task Encryption_NonSensitiveOutputVar_PersistedAsPlaintextForOperatorInspection()
    {
        // Operators need to see non-sensitive output vars when debugging stuck
        // deploys. Encrypting them all would block that workflow without
        // security benefit. P0-3 explicitly preserves plaintext for non-sensitive.
        const string publicValue = "release-2026-05-10-build-42";
        const string varName = "Squid.Action.Build.Version";

        SetCaptureFactoryToEmit(varName, publicValue, sensitive: false);

        var serverTaskId = await SeedSingleStepDeployAsync();
        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync(serverTaskId);

        var json = await GetCheckpointJsonAsync(serverTaskId);

        json.ShouldContain(publicValue,
            customMessage: "Non-sensitive output values MUST be plaintext in the checkpoint JSON. " +
                          "Operators inspecting stuck deploys need to read these values; encrypting them all " +
                          "blocks debug workflow without security benefit.");
        json.ShouldNotContain("SQUID_ENCRYPTED",
            customMessage: "Non-sensitive values MUST NOT be encrypted; that's the documented contract.");
    }

    [Fact]
    public async Task Encryption_SensitiveValueDecryptsCorrectly_OnResumePhase()
    {
        // Round-trip: emit → encrypt → persist → restore → decrypt → original.
        // After the pipeline finishes, manually invoke ResumeCheckpointPhase
        // against a fresh DI scope (simulating a server restart) and verify
        // the decrypted value matches the original.
        const string sensitiveValue = "decrypt-roundtrip-target-99";
        const string varName = "Squid.Action.RoundTrip.Token";

        SetCaptureFactoryToEmit(varName, sensitiveValue, sensitive: true);

        var serverTaskId = await SeedSingleStepDeployAsync();
        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync(serverTaskId);

        // Now simulate a "server restart" — start a fresh DeploymentTaskContext
        // and run JUST the ResumeCheckpointPhase against the persisted state.
        // This proves the decrypt path works end-to-end with the real DB +
        // real DI-resolved IVariableEncryptionService.
        var restored = await SimulateResumeAndGetRestoredVariablesAsync(serverTaskId);

        var matchedVariable = restored.SingleOrDefault(v =>
            string.Equals(v.Name, varName, StringComparison.OrdinalIgnoreCase));

        matchedVariable.ShouldNotBeNull(
            customMessage: $"Restored output variables did not contain {varName}. " +
                          "The persist → restore round-trip lost the variable somewhere; check OutputVariableMerger " +
                          "+ SerializeOutputVariables + RestoreOutputVariablesAsync.");

        matchedVariable.Value.ShouldBe(sensitiveValue,
            customMessage: $"Decrypted value mismatch. Expected '{sensitiveValue}' got '{matchedVariable.Value}'. " +
                          "If the value is the SQUID_ENCRYPTED ciphertext, decryption was skipped — check " +
                          "ResumeCheckpointPhase.RestoreOutputVariablesAsync + IsValidEncryptedValue guard.");

        matchedVariable.IsSensitive.ShouldBeTrue(
            customMessage: "IsSensitive flag MUST round-trip — operators rely on this to mask the value in logs / UI.");
    }

    // ────────────────────────────────────────────────────────────────────────
    // 2. BACKWARD COMPAT — pre-fix plaintext checkpoints must still resume
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BackwardCompat_LegacyPlaintextCheckpoint_ResumesUnchanged()
    {
        // Operators upgrading 1.6.5 → 1.6.6 mid-deployment have checkpoint
        // rows with plaintext sensitive values (no SQUID_ENCRYPTED prefix).
        // RestoreOutputVariablesAsync's IsValidEncryptedValue guard must
        // pass these through unchanged. Without this, the upgrade breaks
        // every active deploy.
        const string legacyPlaintextValue = "pre-fix-plaintext-secret-from-1.6.5";

        var serverTaskId = await SeedTaskAsync(targetCount: 1, scriptBody: "echo hi");

        // Manually plant a pre-fix checkpoint row directly in the DB.
        // Mimics the shape that 1.6.5's SerializeOutputVariables produced
        // — VariableDto[] with IsSensitive=true and plaintext Value.
        var legacyJson = JsonSerializer.Serialize(new[]
        {
            new VariableDto
            {
                Name = "Squid.Action.LegacyDeploy.OldSecret",
                Value = legacyPlaintextValue,
                IsSensitive = true
            }
        });

        await PlantCheckpointAsync(serverTaskId, legacyJson);

        // Resume → restore should pass through unchanged because the value
        // doesn't have the SQUID_ENCRYPTED prefix.
        var restored = await SimulateResumeAndGetRestoredVariablesAsync(serverTaskId);

        var legacyVar = restored.SingleOrDefault(v => v.Name == "Squid.Action.LegacyDeploy.OldSecret");
        legacyVar.ShouldNotBeNull("Pre-fix legacy checkpoint var must restore unchanged.");
        legacyVar.Value.ShouldBe(legacyPlaintextValue,
            customMessage: "1.6.5 → 1.6.6 upgrade compat broken: pre-fix plaintext value mangled by decrypt path. " +
                          "Verify IsValidEncryptedValue(plaintext-with-no-prefix) returns false → skip decrypt.");
    }

    // ────────────────────────────────────────────────────────────────────────
    // 3. MULTI-BATCH CHECKPOINT PROGRESSION
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MultiBatch_LastCompletedBatchIndex_AdvancesAfterEachBatch()
    {
        // 3 sequential steps = 3 batches (each step its own batch with default
        // StartTrigger). Final checkpoint should have LastCompletedBatchIndex=2.
        ExecutionCapture.Clear();
        ExecutionCapture.ResultFactory = _ => new ScriptExecutionResult { Success = true, ExitCode = 0 };

        var serverTaskId = await SeedThreeStepDeployAsync();
        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync(serverTaskId);

        var checkpoint = await LoadCheckpointAsync(serverTaskId);

        checkpoint.ShouldNotBeNull("Checkpoint MUST be persisted at least once for a multi-batch deploy.");
        checkpoint.LastCompletedBatchIndex.ShouldBe(2,
            customMessage: $"3-batch deploy should leave LastCompletedBatchIndex=2 (zero-indexed). " +
                          $"Got {checkpoint.LastCompletedBatchIndex}. " +
                          "Lower = persist skipped a batch; higher = stale value from a prior test.");
    }

    [Fact]
    public async Task MultiBatch_OutputVariables_AccumulateAcrossBatches()
    {
        // Step 1 emits A=alpha, Step 2 emits B=bravo, Step 3 emits C=charlie.
        // Final checkpoint should have all three (P0-2's OutputVariableMerger
        // is what makes this safe across multiple batches).
        var emissions = new Dictionary<int, (string Name, string Value, bool Sensitive)>
        {
            [0] = ("Squid.Action.Step1.A", "alpha-value", false),
            [1] = ("Squid.Action.Step2.B", "bravo-value", false),
            [2] = ("Squid.Action.Step3.C", "charlie-secret", true)
        };

        var stepCallCount = 0;
        ExecutionCapture.Clear();
        ExecutionCapture.ResultFactory = req =>
        {
            // Each captured request = one step's execution. Use the index to
            // pick the right output var. (Order is preserved by the fixture's
            // single-target seed.)
            var idx = Interlocked.Increment(ref stepCallCount) - 1;
            if (!emissions.TryGetValue(idx, out var emit))
                return new ScriptExecutionResult { Success = true, ExitCode = 0 };

            var sensFlag = emit.Sensitive ? "True" : "False";
            return new ScriptExecutionResult
            {
                Success = true,
                ExitCode = 0,
                LogLines = new List<string>
                {
                    $"##squid[setVariable name='{emit.Name}' value='{emit.Value}' sensitive='{sensFlag}']"
                }
            };
        };

        var serverTaskId = await SeedThreeStepDeployAsync();
        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync(serverTaskId);

        var json = await GetCheckpointJsonAsync(serverTaskId);
        json.ShouldNotBeNullOrEmpty("Checkpoint OutputVariablesJson must contain accumulated state.");

        // Each non-sensitive value plaintext; sensitive value encrypted.
        json.ShouldContain("alpha-value",
            customMessage: "Step 1 non-sensitive output var lost between batches. OutputVariableMerger may be discarding entries.");
        json.ShouldContain("bravo-value",
            customMessage: "Step 2 non-sensitive output var lost between batches.");
        json.ShouldNotContain("charlie-secret",
            customMessage: "P0-3 REGRESSION: Step 3 sensitive value leaked as plaintext in checkpoint.");
        json.ShouldContain("SQUID_ENCRYPTED",
            customMessage: "Sensitive Step 3 value should be encrypted — prefix missing means encryption was skipped.");

        // Restored variables should have all three when resumed.
        var restored = await SimulateResumeAndGetRestoredVariablesAsync(serverTaskId);
        restored.Count(v => v.Name.StartsWith("Squid.Action.Step", StringComparison.Ordinal))
            .ShouldBe(3,
                customMessage: "All 3 output vars from 3 batches must restore. " +
                              "Missing entries = OutputVariableMerger lost them or RestoreOutputVariables filtered them out.");

        var restoredCharlie = restored.Single(v => v.Name == "Squid.Action.Step3.C");
        restoredCharlie.Value.ShouldBe("charlie-secret",
            customMessage: "Sensitive Step 3 value didn't decrypt back to plaintext on resume.");
    }

    // ────────────────────────────────────────────────────────────────────────
    // 4. RESUME SEMANTICS — planted checkpoint causes batch skip
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlantedCheckpoint_WithLastCompletedBatchIndex0_SkipsFirstBatch()
    {
        // Plant a checkpoint claiming batch 0 is complete; set the task to
        // Paused. ProcessAsync should resume from batch 1, NOT re-execute
        // batch 0.
        ExecutionCapture.Clear();
        ExecutionCapture.ResultFactory = _ => new ScriptExecutionResult { Success = true, ExitCode = 0 };

        var serverTaskId = await SeedThreeStepDeployAsync();

        // Simulate a prior run that completed batch 0 and crashed.
        await PlantCheckpointAsync(serverTaskId, outputVariablesJson: "[]", lastCompletedBatchIndex: 0);
        await SetTaskStateAsync(serverTaskId, TaskState.Paused);

        await ExecutePipelineAsync(serverTaskId);

        ExecutionCapture.CapturedRequests.Count.ShouldBe(2,
            customMessage: "After resume from batch 0, only batches 1 + 2 should re-execute (2 captured requests). " +
                          $"Got {ExecutionCapture.CapturedRequests.Count}. " +
                          "If 3: resume didn't honor LastCompletedBatchIndex (re-ran batch 0). " +
                          "If <2: pipeline incorrectly skipped post-resume batches.");
    }

    [Fact]
    public async Task PlantedCheckpoint_WithLastCompletedBatchIndex1_OnlyLastBatchExecutes()
    {
        ExecutionCapture.Clear();
        ExecutionCapture.ResultFactory = _ => new ScriptExecutionResult { Success = true, ExitCode = 0 };

        var serverTaskId = await SeedThreeStepDeployAsync();

        await PlantCheckpointAsync(serverTaskId, outputVariablesJson: "[]", lastCompletedBatchIndex: 1);
        await SetTaskStateAsync(serverTaskId, TaskState.Paused);

        await ExecutePipelineAsync(serverTaskId);

        ExecutionCapture.CapturedRequests.Count.ShouldBe(1,
            customMessage: "After resume from batch 1, only batch 2 should execute. " +
                          $"Got {ExecutionCapture.CapturedRequests.Count}.");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private void SetCaptureFactoryToEmit(string varName, string value, bool sensitive)
    {
        ExecutionCapture.Clear();
        var sensFlag = sensitive ? "True" : "False";
        ExecutionCapture.ResultFactory = _ => new ScriptExecutionResult
        {
            Success = true,
            ExitCode = 0,
            LogLines = new List<string>
            {
                $"##squid[setVariable name='{varName}' value='{value}' sensitive='{sensFlag}']"
            }
        };
    }

    private async Task ExecutePipelineAsync(int serverTaskId)
    {
        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task AssertTaskSuccessAsync(int serverTaskId)
    {
        await _fixture.Run<IServerTaskDataProvider>(async provider =>
        {
            var tasks = await provider.GetAllServerTasksAsync(CancellationToken.None).ConfigureAwait(false);
            var task = tasks.SingleOrDefault(t => t.Id == serverTaskId);
            task.ShouldNotBeNull($"ServerTask {serverTaskId} not found in DB.");
            task.State.ShouldBe(TaskState.Success,
                customMessage: $"Task {serverTaskId} ended in {task.State}. " +
                              "Inspect deployment-execution-checkpoint table + activity_log for the failure cause.");
        }).ConfigureAwait(false);
    }

    private async Task<DeploymentExecutionCheckpoint> LoadCheckpointAsync(int serverTaskId)
    {
        return await _fixture.Run<IDeploymentCheckpointService, DeploymentExecutionCheckpoint>(async svc =>
            await svc.LoadAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private async Task<string> GetCheckpointJsonAsync(int serverTaskId)
    {
        var checkpoint = await LoadCheckpointAsync(serverTaskId);
        checkpoint.ShouldNotBeNull(
            customMessage: $"No DeploymentExecutionCheckpoint row found for task {serverTaskId}. " +
                          "Either the deploy didn't reach PersistCheckpointAsync or it failed silently. " +
                          "Verify P0-5 retry isn't swallowing all attempts (check Log.Error for retry exhaustion).");
        return checkpoint.OutputVariablesJson;
    }

    private async Task<List<VariableDto>> SimulateResumeAndGetRestoredVariablesAsync(int serverTaskId)
    {
        // Build a fresh ResumeCheckpointPhase manually (real services from
        // the DI scope) and execute it. This proves the decrypt path works
        // end-to-end with the real IVariableEncryptionService that DI
        // resolves — different from the unit test's mocked encryption.
        var restored = new List<VariableDto>();

        await _fixture.Run<ResumeCheckpointPhase>(async phase =>
        {
            var ctx = new DeploymentTaskContext { ServerTaskId = serverTaskId };
            await phase.ExecuteAsync(ctx, CancellationToken.None).ConfigureAwait(false);
            restored.AddRange(ctx.RestoredOutputVariables);
        }).ConfigureAwait(false);

        return restored;
    }

    private async Task PlantCheckpointAsync(int serverTaskId, string outputVariablesJson, int lastCompletedBatchIndex = 0)
    {
        await _fixture.Run<IDeploymentCheckpointService>(async svc =>
        {
            await svc.SaveAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = serverTaskId,
                DeploymentId = 1,
                LastCompletedBatchIndex = lastCompletedBatchIndex,
                FailureEncountered = false,
                OutputVariablesJson = outputVariablesJson,
                BatchStatesJson = "{}",
                InFlightScriptsJson = "{}"
            }, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task SetTaskStateAsync(int serverTaskId, string newState)
    {
        // Direct DB poke — the service-level transition would enforce state
        // machine rules. For test setup we want raw control.
        await _fixture.Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            await repo.ExecuteUpdateAsync<ServerTask>(
                t => t.Id == serverTaskId,
                s => s.SetProperty(t => t.State, newState),
                CancellationToken.None).ConfigureAwait(false);
            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    // ── Seeders ────────────────────────────────────────────────────────────

    private async Task<int> SeedSingleStepDeployAsync()
    {
        return await SeedTaskAsync(targetCount: 1, scriptBody: "echo emit-sensitive-output");
    }

    private async Task<int> SeedThreeStepDeployAsync()
    {
        return await SeedTaskAsync(targetCount: 1, stepCount: 3, scriptBody: "echo step");
    }

    private async Task<int> SeedTaskAsync(int targetCount, string scriptBody, int stepCount = 1)
    {
        ExecutionCapture.CapturedRequests.Clear();

        var taskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            await builder.CreateVariablesAsync(variableSet.Id,
                ("AppEnv", "e2e-resume", VariableType.String, false)).ConfigureAwait(false);

            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            for (var s = 0; s < stepCount; s++)
            {
                var step = await builder.CreateDeploymentStepAsync(process.Id, s + 1, $"Step{s + 1}").ConfigureAwait(false);
                await builder.CreateStepPropertiesAsync(step.Id,
                    ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

                var action = await builder.CreateDeploymentActionAsync(
                    step.Id, 1, $"Action{s + 1}", actionType: "Squid.Script").ConfigureAwait(false);

                await builder.CreateActionMachineRolesAsync(action.Id, "k8s").ConfigureAwait(false);
                await builder.CreateActionPropertiesAsync(action.Id,
                    ("Squid.Action.Script.ScriptBody", scriptBody),
                    ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);
            }

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync("E2E Resume Environment").ConfigureAwait(false);

            for (var i = 0; i < targetCount; i++)
            {
                var endpointJson = JsonSerializer.Serialize(new
                {
                    CommunicationStyle = "KubernetesApi",
                    ClusterUrl = "https://localhost:6443",
                    SkipTlsVerification = "True",
                    Namespace = "default",
                    ResourceReferences = new[]
                    {
                        new { Type = (int)EndpointResourceType.AuthenticationAccount, ResourceId = 1 }
                    }
                });

                var machine = new Machine
                {
                    Name = $"E2E Resume Target {i}",
                    IsDisabled = false,
                    Roles = DeploymentTargetFinder.SerializeRoles(new[] { "k8s" }),
                    EnvironmentIds = DeploymentTargetFinder.SerializeIds(new[] { environment.Id }),
                    Endpoint = endpointJson,
                    SpaceId = 1,
                    Slug = $"e2e-resume-target-{i}-{Guid.NewGuid().ToString("N")[..6]}"
                };
                await repository.InsertAsync(machine, CancellationToken.None).ConfigureAwait(false);
            }
            await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var account = new DeploymentAccount
            {
                SpaceId = 1,
                Name = "E2E Resume Account",
                Slug = "e2e-resume-account",
                AccountType = AccountType.Token,
                Credentials = DeploymentAccountCredentialsConverter.Serialize(
                    new TokenCredentials { Token = "e2e-resume-token" })
            };
            await repository.InsertAsync(account, CancellationToken.None).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);
            var deployment = new Deployment
            {
                Name = "E2E Resume Deployment",
                SpaceId = 1, ChannelId = channel.Id, ProjectId = project.Id,
                ReleaseId = release.Id, EnvironmentId = environment.Id,
                DeployedBy = 1, CreatedDate = DateTimeOffset.UtcNow, Json = string.Empty
            };
            await repository.InsertAsync(deployment, CancellationToken.None).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var serverTask = new ServerTask
            {
                Name = "E2E Resume Task", Description = "E2E resume test",
                QueueTime = DateTimeOffset.UtcNow, State = TaskState.Pending,
                ServerTaskType = "Deploy", ProjectId = project.Id, EnvironmentId = environment.Id,
                SpaceId = 1, LastModifiedDate = DateTimeOffset.UtcNow,
                BusinessProcessState = "Queued", StateOrder = 1, Weight = 1, BatchId = 0,
                JSON = string.Empty, HasWarningsOrErrors = false,
                ServerNodeId = Guid.NewGuid(), DurationSeconds = 0, DataVersion = Array.Empty<byte>()
            };
            await repository.InsertAsync(serverTask, CancellationToken.None).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            deployment.TaskId = serverTask.Id;
            await repository.UpdateAsync(deployment, CancellationToken.None).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            taskId = serverTask.Id;
        }).ConfigureAwait(false);

        return taskId;
    }
}
